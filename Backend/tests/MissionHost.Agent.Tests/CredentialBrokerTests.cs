using System.Diagnostics;
using System.Text;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.MissionHost.CredentialBroker;

namespace Blackglass.MissionHost.Agent.Tests;

/// <summary>
/// The root credential broker (security correction I3): the only interface is a canonical attempt id, a generation and the
/// credential bytes; the store is a fixed root-only directory; links, other file types, wrong owners or modes fail closed and are
/// never read or written through; failed writes, generation mismatches and concurrent preparations leave a consistent store.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CredentialBrokerTests : IDisposable
{
    const string Attempt = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41";
    readonly string root, outside;
    readonly uint owner = Native.EffectiveUid;
    readonly StringWriter log = new();

    public CredentialBrokerTests()
    {
        var parent = Path.Combine(Path.GetTempPath(), "bg-broker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        root = Path.Combine(parent, "credentials");
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Something a hostile request would like to reach: a "sensitive" file and directory outside the store.
        outside = Path.Combine(parent, "sensitive");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "process"), "SENSITIVE-ROOT-FILE");
        File.WriteAllText(Path.Combine(outside, "generation"), "1");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    CredentialStore Store(Faults? faults = null) => new(new BrokerOptions(root, owner), log, faults);

    static string Credential() => OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix).Text;

    string ProcessPath => Path.Combine(root, Attempt, "process");

    string[] Names() => [.. Directory.EnumerateFileSystemEntries(Path.Combine(root, Attempt)).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    string SensitiveText => File.ReadAllText(Path.Combine(outside, "process"));

    static byte[] Prepare(string attempt, string generation, string credential) => Encoding.ASCII.GetBytes($"prepare {attempt} {generation}\n{credential}\n");

    // ---- the request: only a canonical attempt, a generation and a pc1 credential ----

    [Fact]
    public void A_well_formed_prepare_and_discard_parse()
    {
        var credential = Credential();
        var prepare = Assert.IsType<BrokerRequest.Prepare>(BrokerRequest.Parse(Prepare(Attempt, "3", credential)));
        Assert.Equal((Attempt, 3, credential), (prepare.Attempt, prepare.Generation, prepare.Credential));
        Assert.Equal(Attempt, Assert.IsType<BrokerRequest.Discard>(BrokerRequest.Parse(Encoding.ASCII.GetBytes($"discard {Attempt}\n"))).Attempt);
    }

    [Theory]
    [InlineData("prepare 0192F1A4-5C2E-7B1D-9A0E-3F6C1D2B8E41 1\n{c}\n", "bad_attempt")] // not canonical lowercase
    [InlineData("prepare 00000000-0000-0000-0000-000000000000 1\n{c}\n", "bad_attempt")]
    [InlineData("prepare ../../etc/shadow 1\n{c}\n", "bad_attempt")]
    [InlineData("prepare /etc/shadow 1\n{c}\n", "bad_attempt")]
    [InlineData("prepare 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41/../x 1\n{c}\n", "bad_attempt")]
    [InlineData("prepare 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41 0\n{c}\n", "bad_generation")]
    [InlineData("prepare 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41 -1\n{c}\n", "bad_generation")]
    [InlineData("prepare 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41 01\n{c}\n", "bad_generation")]
    [InlineData("prepare 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41 99999999999\n{c}\n", "bad_generation")]
    [InlineData("prepare 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41 1\nhs1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n", "bad_credential")] // a host secret
    [InlineData("prepare 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41 1\n/etc/shadow\n", "bad_credential")] // a path is not a credential
    [InlineData("prepare 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41 1\npc1_short\n", "bad_credential")]
    [InlineData("prepare 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41 1\n{c}\nextra\n", "malformed")]
    [InlineData("prepare 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41 1\n{c}", "malformed")] // no final newline
    [InlineData("prepare 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41 1\r\n{c}\r\n", "malformed")]
    [InlineData("prepare  0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41 1\n{c}\n", "malformed")]
    [InlineData("prepare 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41\n{c}\n", "malformed")]
    [InlineData("read 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41\n", "malformed")] // no other verb exists
    [InlineData("copy /etc/shadow /run/blackglass/credentials/x/process\n", "malformed")]
    [InlineData("discard ../x\n", "bad_attempt")]
    [InlineData("", "malformed")]
    public void Anything_else_is_refused_with_a_code(string template, string code)
    {
        var text = template.Replace("{c}", Credential(), StringComparison.Ordinal);
        Assert.Equal(code, Assert.IsType<BrokerRequest.Invalid>(BrokerRequest.Parse(Encoding.ASCII.GetBytes(text))).Code);
    }

    [Fact]
    public void An_oversized_request_or_a_nul_byte_is_refused()
    {
        var big = new byte[BrokerRequest.MaxBytes + 1];
        Array.Fill(big, (byte)'a');
        Assert.Equal("too_large", Assert.IsType<BrokerRequest.Invalid>(BrokerRequest.Parse(big)).Code);
        var nul = Prepare(Attempt, "1", Credential());
        nul[20] = 0;
        Assert.IsType<BrokerRequest.Invalid>(BrokerRequest.Parse(nul));
    }

    // ---- preparation ----

    [Fact]
    public void A_prepared_credential_is_exactly_the_bytes_in_a_root_only_directory_and_file()
    {
        var credential = Credential();
        Assert.Equal(BrokerCode.Ok, Store().Prepare(Attempt, 3, credential));
        Assert.Equal(credential, File.ReadAllText(ProcessPath));
        Assert.Equal("3", File.ReadAllText(Path.Combine(root, Attempt, "generation")));
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(ProcessPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(root, Attempt)));
        var stat = Native.LStat(ProcessPath)!.Value;
        Assert.Equal((owner, 1UL, true), (stat.Uid, stat.Nlink, stat.IsRegular));
        Assert.Equal(new[] { "generation", "process" }, Names());
        Assert.DoesNotContain(credential, log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Generations_only_move_forward_and_a_conflicting_same_generation_is_refused()
    {
        var store = Store();
        string first = Credential(), second = Credential(), third = Credential();
        Assert.Equal(BrokerCode.Ok, store.Prepare(Attempt, 2, first));
        Assert.Equal(BrokerCode.Ok, store.Prepare(Attempt, 2, first)); // a retry: idempotent
        Assert.Equal(BrokerCode.Conflict, store.Prepare(Attempt, 2, second));
        Assert.Equal(BrokerCode.Stale, store.Prepare(Attempt, 1, second));
        Assert.Equal(first, File.ReadAllText(ProcessPath));
        Assert.Equal(BrokerCode.Ok, store.Prepare(Attempt, 3, third)); // a re-claim after a supersede
        Assert.Equal(third, File.ReadAllText(ProcessPath));
        Assert.Equal("3", File.ReadAllText(Path.Combine(root, Attempt, "generation")));
    }

    [Fact]
    public void Discard_removes_the_credential_and_is_idempotent()
    {
        var store = Store();
        Assert.Equal(BrokerCode.Ok, store.Prepare(Attempt, 1, Credential()));
        Assert.Equal(BrokerCode.Ok, store.Discard(Attempt));
        Assert.False(Directory.Exists(Path.Combine(root, Attempt)));
        Assert.Equal(BrokerCode.Ok, store.Discard(Attempt));
    }

    // ---- an unsafe store fails closed and is never followed ----

    [Fact]
    public void A_store_that_is_a_link_is_missing_is_open_to_others_or_has_another_owner_is_refused()
    {
        var credential = Credential();
        var link = root + "-link";
        File.CreateSymbolicLink(link, outside);
        Assert.Equal(BrokerCode.UnsafeStore, new CredentialStore(new BrokerOptions(link, owner), log).Prepare(Attempt, 1, credential));
        Assert.Equal(BrokerCode.UnsafeStore, new CredentialStore(new BrokerOptions(root + "-missing", owner), log).Prepare(Attempt, 1, credential));
        Assert.Equal(BrokerCode.UnsafeStore, new CredentialStore(new BrokerOptions(root, owner + 1), log).Prepare(Attempt, 1, credential));
        File.SetUnixFileMode(root, (UnixFileMode)0b111_101_101);
        Assert.Equal(BrokerCode.UnsafeStore, Store().Prepare(Attempt, 1, credential));
        Assert.Equal("SENSITIVE-ROOT-FILE", SensitiveText);
        Assert.False(File.Exists(Path.Combine(outside, Attempt, "process")));
    }

    [Fact]
    public void An_attempt_entry_that_is_a_link_to_a_sensitive_directory_is_never_written_through()
    {
        File.CreateSymbolicLink(Path.Combine(root, Attempt), outside);
        Assert.Equal(BrokerCode.UnsafeStore, Store().Prepare(Attempt, 5, Credential()));
        Assert.Equal(BrokerCode.UnsafeStore, Store().Discard(Attempt));
        Assert.Equal("SENSITIVE-ROOT-FILE", SensitiveText);
        Assert.Equal("1", File.ReadAllText(Path.Combine(outside, "generation")));
    }

    [Fact]
    public void A_link_to_an_empty_private_directory_of_the_same_owner_is_never_written_through()
    {
        // The hardest case: the target would pass every owner, mode and content check if the link were followed.
        var target = Path.Combine(Path.GetDirectoryName(root)!, "private-target");
        Directory.CreateDirectory(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.CreateSymbolicLink(Path.Combine(root, Attempt), target);
        Assert.Equal(BrokerCode.UnsafeStore, Store().Prepare(Attempt, 1, Credential()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void A_hard_link_is_refused_by_the_read_itself_too()
    {
        // Two independent checks refuse a second link (the entry's lstat and the read's fstat); this pins the read's.
        Assert.Equal(BrokerCode.Ok, Store().Prepare(Attempt, 1, Credential()));
        Assert.NotNull(Native.ReadPrivate(ProcessPath, owner, 512));
        Native.Link(ProcessPath, Path.Combine(Path.GetDirectoryName(root)!, "another-name"));
        Assert.Null(Native.ReadPrivate(ProcessPath, owner, 512));
    }

    [Fact]
    public void An_attempt_entry_that_is_a_file_is_refused()
    {
        File.WriteAllText(Path.Combine(root, Attempt), "x");
        Assert.Equal(BrokerCode.UnsafeStore, Store().Prepare(Attempt, 1, Credential()));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("generation")]
    public void A_credential_file_that_is_a_link_to_a_sensitive_file_is_refused_unread_and_unchanged(string name)
    {
        Directory.CreateDirectory(Path.Combine(root, Attempt), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.CreateSymbolicLink(Path.Combine(root, Attempt, name), Path.Combine(outside, name));
        Assert.Equal(BrokerCode.UnsafeStore, Store().Prepare(Attempt, 1, Credential()));
        Assert.Equal("SENSITIVE-ROOT-FILE", SensitiveText);
        Assert.Equal("1", File.ReadAllText(Path.Combine(outside, "generation")));
        Assert.DoesNotContain("SENSITIVE", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_hard_linked_credential_file_is_refused()
    {
        Assert.Equal(BrokerCode.Ok, Store().Prepare(Attempt, 1, Credential()));
        Native.Link(ProcessPath, Path.Combine(Path.GetDirectoryName(root)!, "second-name"));
        Assert.Equal(BrokerCode.UnsafeStore, Store().Prepare(Attempt, 2, Credential()));
    }

    [Fact]
    public void A_fifo_or_a_wrongly_permissioned_credential_file_is_refused_without_blocking()
    {
        Directory.CreateDirectory(Path.Combine(root, Attempt), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Native.MkFifo(ProcessPath);
        var watch = Stopwatch.StartNew();
        Assert.Equal(BrokerCode.UnsafeStore, Store().Prepare(Attempt, 1, Credential()));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        File.Delete(ProcessPath);
        File.WriteAllText(ProcessPath, Credential());
        File.SetUnixFileMode(ProcessPath, (UnixFileMode)0b110_100_100); // 0644: readable by others
        Assert.Equal(BrokerCode.UnsafeStore, Store().Prepare(Attempt, 1, Credential()));
    }

    [Fact]
    public void A_credential_file_owned_by_someone_else_is_refused()
    {
        if (owner != 0) return; // only root can give a file away; the VM runner's container runs as root
        Assert.Equal(BrokerCode.Ok, Store().Prepare(Attempt, 1, Credential()));
        Native.Chown(ProcessPath, 1234, 1234);
        Assert.Equal(BrokerCode.UnsafeStore, Store().Prepare(Attempt, 2, Credential()));
    }

    // ---- failed writes and concurrency ----

    [Theory]
    [InlineData("process:write")]
    [InlineData("process:rename")]
    [InlineData("generation:write")]
    [InlineData("generation:rename")]
    public void A_failed_write_fails_closed_leaves_no_temporary_file_and_a_retry_succeeds(string point)
    {
        string old = Credential(), next = Credential();
        Assert.Equal(BrokerCode.Ok, Store().Prepare(Attempt, 1, old));
        Assert.Equal(BrokerCode.WriteFailed, Store(new Faults(point)).Prepare(Attempt, 2, next));
        Assert.Equal(new[] { "generation", "process" }, Names());
        var generation = File.ReadAllText(Path.Combine(root, Attempt, "generation"));
        Assert.True(generation == "1", "the generation never runs ahead of the credential it describes"); // process may already be new
        Assert.Equal(BrokerCode.Ok, Store().Prepare(Attempt, 2, next));
        Assert.Equal(next, File.ReadAllText(ProcessPath));
    }

    [Fact]
    public async Task A_preparation_holds_the_store_so_another_waits_for_it()
    {
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => Store(new Faults("process:write", () => { inside.Set(); release.Wait(TimeSpan.FromSeconds(10)); })).Prepare(Attempt, 2, Credential()));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(() => Store().Prepare(Attempt, 3, Credential()));
        await Task.Delay(500);
        Assert.False(second.IsCompleted); // it waits on the store's lock while the first is inside
        release.Set();
        Assert.Equal((BrokerCode.Ok, BrokerCode.Ok), (await first, await second));
        Assert.Equal("3", File.ReadAllText(Path.Combine(root, Attempt, "generation")));
    }

    [Fact]
    public async Task Concurrent_preparations_are_serialized_and_end_at_the_highest_generation()
    {
        var credentials = Enumerable.Range(1, 24).ToDictionary(g => g, _ => Credential());
        var results = await Task.WhenAll(credentials.Select(kv => Task.Run(() => (kv.Key, Code: Store().Prepare(Attempt, kv.Key, kv.Value)))));
        Assert.All(results, r => Assert.Contains(r.Code, new[] { BrokerCode.Ok, BrokerCode.Stale }));
        Assert.Contains(results, r => r is { Key: 24, Code: BrokerCode.Ok });
        Assert.Equal("24", File.ReadAllText(Path.Combine(root, Attempt, "generation")));
        Assert.Equal(credentials[24], File.ReadAllText(ProcessPath));
        Assert.Equal(new[] { "generation", "process" }, Names());
    }

    // ---- the connection handler ----

    [Fact]
    public void Only_the_agent_peer_is_served_and_the_answer_never_echoes_the_credential()
    {
        var credential = Credential();
        var refused = Broker.Handle(Prepare(Attempt, "1", credential), peerUid: 4242, agentUid: 4243, Store(), log);
        Assert.Equal("error peer_refused\n", Encoding.ASCII.GetString(refused));
        Assert.False(Directory.Exists(Path.Combine(root, Attempt)));
        var ok = Broker.Handle(Prepare(Attempt, "1", credential), peerUid: 4243, agentUid: 4243, Store(), log);
        Assert.Equal("ok\n", Encoding.ASCII.GetString(ok));
        var bad = Broker.Handle(Encoding.ASCII.GetBytes("prepare ../../etc/shadow 1\nx\n"), 4243, 4243, Store(), log);
        Assert.Equal("error bad_attempt\n", Encoding.ASCII.GetString(bad));
        Assert.DoesNotContain(credential, log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_agent_uid_is_read_from_the_password_file_only_for_the_exact_name()
    {
        const string passwd = "root:x:0:0:root:/root:/bin/bash\nblackglass-agent2:x:998:998::/x:/usr/sbin/nologin\nblackglass-agent:x:997:997:Blackglass:/var/lib/blackglass/agent:/usr/sbin/nologin\n";
        Assert.Equal(997u, Broker.AgentUid(passwd));
        Assert.Null(Broker.AgentUid("root:x:0:0:root:/root:/bin/bash\n"));
        Assert.Null(Broker.AgentUid("blackglass-agent:x:0:0::/:/bin/sh\n")); // never root
    }
}
