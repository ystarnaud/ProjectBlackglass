using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Blackglass.Accounts.IntegrationTests.E2E;

namespace Blackglass.Accounts.IntegrationTests.Unit;

/// <summary>
/// CP4's loopback relay between the Unity mission server and the API: requests and answers pass unchanged, one chosen answer
/// is lost after the API handled its request (the lost start response), an unreachable API looks like a dropped connection,
/// and the log names only method, path and status: never a header or a body.
/// </summary>
public sealed class LostResponseProxyTests : IAsyncLifetime
{
    WebApplication upstream = null!;
    int upstreamPort;
    readonly List<(string Path, string Authorization, string Body)> seen = [];

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        upstream = builder.Build();
        upstream.MapPost("/{**path}", async (HttpContext http) =>
        {
            var body = await new StreamReader(http.Request.Body).ReadToEndAsync();
            lock (seen) seen.Add((http.Request.Path.Value!, http.Request.Headers.Authorization.ToString(), body));
            http.Response.Headers["X-Upstream"] = "yes";
            return Results.Json(new { echo = body.Length }, statusCode: http.Request.Path.Value!.EndsWith("/conflict", StringComparison.Ordinal) ? 409 : 200);
        });
        await upstream.StartAsync();
        upstreamPort = new Uri(upstream.Urls.Single()).Port;
    }

    public async Task DisposeAsync() => await upstream.DisposeAsync();

    static async Task<HttpResponseMessage> PostAsync(LostResponseProxy proxy, string path, string body, string? authorization = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri(proxy.BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task Requests_and_answers_pass_unchanged()
    {
        await using var proxy = new LostResponseProxy(upstreamPort);

        using var response = await PostAsync(proxy, "/server/v1/attempts/a/ready", "{\"x\":1}", "Bearer pc1_abc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"echo\":7}", await response.Content.ReadAsStringAsync());
        Assert.Equal("yes", response.Headers.GetValues("X-Upstream").Single());
        Assert.Equal(("/server/v1/attempts/a/ready", "Bearer pc1_abc", "{\"x\":1}"), seen.Single());
        Assert.Equal(("POST", "/server/v1/attempts/a/ready", 200, "answered"), Entry(proxy.Log.Single()));
    }

    [Fact]
    public async Task A_refusal_passes_with_its_status()
    {
        await using var proxy = new LostResponseProxy(upstreamPort);
        using var response = await PostAsync(proxy, "/x/conflict", "{}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(409, proxy.Log.Single().Status);
    }

    [Fact]
    public async Task The_next_matching_answer_is_lost_after_the_api_handled_its_request_and_only_that_one()
    {
        await using var proxy = new LostResponseProxy(upstreamPort);
        var dropped = 0;
        proxy.DropNextAnswer("/start", () => Interlocked.Increment(ref dropped));

        await Assert.ThrowsAsync<HttpRequestException>(() => PostAsync(proxy, "/server/v1/attempts/a/participants/b/start", "{\"s\":1}"));
        Assert.Single(seen); // the API received and answered it
        Assert.Equal(1, dropped);

        using var retry = await PostAsync(proxy, "/server/v1/attempts/a/participants/b/start", "{\"s\":1}");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, seen.Count);
        Assert.Equal(1, dropped);
        Assert.Equal([("POST", "/server/v1/attempts/a/participants/b/start", 200, "dropped"), ("POST", "/server/v1/attempts/a/participants/b/start", 200, "answered")],
            proxy.Log.Select(Entry));
    }

    [Fact]
    public async Task Other_routes_are_answered_while_a_drop_waits()
    {
        await using var proxy = new LostResponseProxy(upstreamPort);
        proxy.DropNextAnswer("/start", () => { });
        using var heartbeat = await PostAsync(proxy, "/server/v1/attempts/a/heartbeat", "{}");
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        Assert.Equal("answered", proxy.Log.Single().Outcome);
    }

    [Fact]
    public async Task An_unreachable_api_closes_the_connection_without_an_answer()
    {
        int closed;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); closed = ((IPEndPoint)probe.LocalEndpoint).Port; }
        await using var proxy = new LostResponseProxy(closed);

        await Assert.ThrowsAsync<HttpRequestException>(() => PostAsync(proxy, "/server/v1/attempts/a/recorded", "{}"));

        Assert.Equal(("POST", "/server/v1/attempts/a/recorded", 0, "upstream_unreachable"), Entry(proxy.Log.Single()));
    }

    [Fact]
    public async Task The_log_holds_no_header_body_or_query()
    {
        await using var proxy = new LostResponseProxy(upstreamPort);
        using var response = await PostAsync(proxy, "/server/v1/attempts/a/tickets/redeem?k=v", "{\"ticket\":\"jt1_SECRETTICKET\"}", "Bearer pc1_SECRETCREDENTIAL");
        var text = string.Join("\n", proxy.Log.Select(e => e.ToString()));
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("k=v", text);
        Assert.Equal("/server/v1/attempts/a/tickets/redeem", proxy.Log.Single().Path);
    }

    [Fact]
    public async Task A_client_expecting_100_continue_gets_it_and_its_body_arrives()
    {
        await using var proxy = new LostResponseProxy(upstreamPort);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /p/ready HTTP/1.1\r\nHost: x\r\nContent-Type: application/json\r\nContent-Length: 2\r\nExpect: 100-continue\r\n\r\n"));
        var interim = new byte[25];
        var read = await stream.ReadAsync(interim);
        Assert.StartsWith("HTTP/1.1 100 Continue", Encoding.ASCII.GetString(interim, 0, read));
        await stream.WriteAsync("{}"u8.ToArray());
        var rest = await new StreamReader(stream).ReadToEndAsync();
        Assert.Contains("200 OK", rest);
        Assert.Equal("{}", seen.Single().Body);
    }

    static (string, string, int, string) Entry(ProxyLogEntry e) => (e.Method, e.Path, e.Status, e.Outcome);
}
