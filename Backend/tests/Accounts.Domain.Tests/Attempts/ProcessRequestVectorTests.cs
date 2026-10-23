using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Ids;

namespace Blackglass.Accounts.Domain.Tests.Attempts;

/// <summary>
/// The process-v1 request vectors (Stage 13): the exact bytes Unity's mission process sends (JsonUtility over its DTOs, pinned by
/// Unity's ProcessContractVectorTests) are accepted by the backend's own strict parsers with the expected values. The ready request
/// is attempt-v1 fixture 03's ready file, already judged by the backend's ready path (AttemptFixtureTests).
/// </summary>
public sealed class ProcessRequestVectorTests
{
    static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "contracts", "process-v1");
    const string Session = "0192f1a5-0000-7000-8000-000000000003";

    static byte[] Vector(string name) => File.ReadAllBytes(Path.Combine(Folder, name));

    [Fact]
    public void Register_parses()
    {
        var request = RegisterRequest.Parse(Vector("register.request.json"));
        Assert.Equal("editor", request.Build.Revision);
        Assert.Equal("sha256:e83352b1c9d8764a92281551a25bea42621da2b592bc30d35986a5f0a86c496b", request.Build.CatalogueVersion);
    }

    [Fact]
    public void Ready_is_fixture_03s_ready_report_and_parses()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contracts", "attempt-v1", "03-seeded-collect-and-use.ready.json"));
        var report = ReadyReport.Parse(bytes);
        Assert.Equal(Guid.Parse(Session), report.ProcessSessionId);
        Assert.Equal(LootHash.Compute(report.LootTableVersion, report.LootPool), report.LootHash);
    }

    [Fact]
    public void Redeem_parses()
    {
        var request = RedeemRequest.Parse(Vector("redeem.request.json"));
        Assert.Equal(Guid.Parse(Session), request.ProcessSessionId);
        Assert.Equal("jt1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", request.Ticket);
    }

    [Fact]
    public void Start_parses() => Assert.Equal(Guid.Parse(Session), StartRequest.Parse(Vector("start.request.json")).ProcessSessionId);

    [Fact]
    public void Heartbeat_parses()
    {
        var request = ProcessHeartbeatRequest.Parse(Vector("heartbeat.request.json"));
        Assert.Equal("running", request.Phase);
        Assert.True(Base58Id.TryParse("JG", out var account));
        Assert.Equal([new ParticipantConnection(account, true)], request.Participants);
    }

    [Fact]
    public void Recorded_parses()
    {
        var expected = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contracts", "attempt-v1", "03-seeded-collect-and-use.manifest.json"))));
        Assert.Equal(expected, RecordedRequest.Parse(Vector("recorded.request.json")).ManifestHash);
    }

    [Fact]
    public void Failed_parses()
    {
        var request = FailedRequest.Parse(Vector("failed.request.json"));
        Assert.Equal(Guid.Parse(Session), request.ProcessSessionId);
        Assert.Equal("start_refused", request.Reason);
    }
}
