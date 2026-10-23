using System.Text.Json;
using System.Text.Json.Nodes;
using Blackglass.Accounts.Api.Hosts;
using Blackglass.Accounts.Domain.Ids;

namespace Blackglass.Accounts.Integration.Tests.Unit;

/// <summary>
/// The process-v1 response vectors (Stage 13): the API's own response records, serialized with the API's own options
/// (JsonSerializerDefaults.Web), equal the vectors Unity's mission process parses (Unity's ProcessContractVectorTests), so a backend
/// change that alters the bytes fails here first. No database. The register answer's loadout is built through JsonNode.Parse, as the
/// endpoint builds it from the stored jsonb; its key order is the vector's (Unity proves order doesn't matter with a shuffled one).
/// </summary>
public sealed class ProcessResponseVectorTests
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "contracts", "process-v1");
    const string Session = "0192f1a5-0000-7000-8000-000000000003";
    const string Catalogue = "sha256:e83352b1c9d8764a92281551a25bea42621da2b592bc30d35986a5f0a86c496b";

    static string Vector(string name) => File.ReadAllText(Path.Combine(Folder, name));

    static long Account(string base58) => Base58Id.TryParse(base58, out var id) ? id : throw new ArgumentException(base58);

    static DateTime At(int hour, int minute, int second, long ticks) =>
        new DateTime(2026, 10, 23, hour, minute, second, DateTimeKind.Utc).AddTicks(ticks);

    [Fact]
    public void Register()
    {
        var vector = Vector("register.response.json");
        var loadout = JsonNode.Parse(JsonNode.Parse(vector)!["participants"]![0]!["loadout"]!.ToJsonString())!;
        var response = new ProcessEndpoints.RegisterResponse(Session, 1, new ProcessEndpoints.TemplateDto("procedural-standard", 1), Catalogue,
            [new ProcessEndpoints.ParticipantDto(Account("JG"), loadout)], new ProcessEndpoints.LaunchDeadlineDto(At(12, 0, 0, 1_234_500)));
        Assert.Equal(vector, JsonSerializer.Serialize(response, Web));
    }

    [Fact]
    public void Ready() =>
        Assert.Equal(Vector("ready.response.json"), JsonSerializer.Serialize(new ProcessEndpoints.ReadyResponse(At(12, 2, 0, 5_000_000)), Web));

    [Fact]
    public void Redeem() =>
        Assert.Equal(Vector("redeem.response.json"), JsonSerializer.Serialize(new ProcessEndpoints.RedeemResponse(Account("JG"), ["C", "D"]), Web));

    [Fact]
    public void Start() =>
        Assert.Equal(Vector("start.response.json"), JsonSerializer.Serialize(new ProcessEndpoints.StartResponse(true, At(12, 0, 1, 1_234_560)), Web));

    [Fact]
    public void Recorded() =>
        Assert.Equal(Vector("recorded.response.json"), JsonSerializer.Serialize(new ProcessEndpoints.StateDto("result_pending"), Web));

    [Fact]
    public void Failed() =>
        Assert.Equal(Vector("failed.response.json"), JsonSerializer.Serialize(new AgentEndpoints.AppliedResponse("cancelled"), Web));

    // The heartbeat route answers Results.Text("{}", "application/json").
    [Fact]
    public void Heartbeat() => Assert.Equal("{}", Vector("heartbeat.response.json"));

    [Fact]
    public void Every_vector_timestamp_shape_is_one_the_api_writes()
    {
        foreach (var ticks in new long[] { 0, 1_230_000, 1_234_500, 1_234_560, 1_234_567 })
        {
            var text = JsonSerializer.Serialize(At(12, 0, 0, ticks), Web).Trim('"');
            Assert.Contains(text, JsonNode.Parse(Vector("timestamps.json"))!["valid"]!.AsArray().Select(n => n!.GetValue<string>()));
        }
    }
}
