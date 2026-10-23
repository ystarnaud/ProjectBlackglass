using Blackglass.Accounts.IntegrationTests.Support;

namespace Blackglass.Accounts.IntegrationTests.Unit;

/// <summary>
/// Stage 13 CP4's safe initialization of the shared test cluster (owner's mandatory correction): identity verified before any
/// write, no other database cleaned up, no role created or changed, one uniquely named database created and recorded, and only
/// that database (by name and OID) dropped. Runs on a scripted cluster: no PostgreSQL, so it runs before any real connection.
/// </summary>
public sealed class Cp4DatabaseSafetyTests : IDisposable
{
    const string SystemId = "7412345678901234567";
    readonly string dir = Path.Combine(Path.GetTempPath(), "bg-cp4-safety-" + Guid.NewGuid().ToString("N"));
    readonly FakeCluster cluster = new();
    static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    public Cp4DatabaseSafetyTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string Record => Path.Combine(dir, "cp4-database.json");

    Task<Cp4DatabaseRecord> CreateAsync(string expected = SystemId) => Cp4Database.CreateAsync(cluster, expected, Record, () => Now, () => "0123abcd");

    void AssertNoWrite() => Assert.DoesNotContain(cluster.Calls, c => c.StartsWith("create", StringComparison.Ordinal) || c.StartsWith("drop", StringComparison.Ordinal));

    [Fact]
    public async Task Creates_only_its_uniquely_named_database_after_verifying_the_identity_and_the_roles()
    {
        var record = await CreateAsync();

        Assert.Equal("bg_t_20261023120000_cp4_0123abcd", record.Name);
        Assert.True(Cp4Database.IsCp4Name(record.Name));
        Assert.Equal(["identity", "roles", "oid bg_t_20261023120000_cp4_0123abcd", "create bg_t_20261023120000_cp4_0123abcd owner accounts_migrator",
            "oid bg_t_20261023120000_cp4_0123abcd"], cluster.Calls);
        Assert.Equal(cluster.Databases[record.Name], record.Oid);
        Assert.Equal(SystemId, record.SystemIdentifier);
        Assert.Equal(record, Cp4Database.ReadRecord(Record));
    }

    [Theory]
    [InlineData("blackglass-prod")]
    [InlineData("blackglass-crashdrill")]
    [InlineData("")]
    public async Task Refuses_any_cluster_but_blackglass_test_before_any_write(string name)
    {
        cluster.Identity = cluster.Identity with { ClusterName = name };
        await Assert.ThrowsAnyAsync<Exception>(() => CreateAsync());
        AssertNoWrite();
        Assert.False(File.Exists(Record));
    }

    [Theory]
    [InlineData("1111111111111111111")]
    [InlineData("")]
    public async Task Refuses_an_unexpected_or_missing_system_identifier_before_any_write(string expected)
    {
        var e = await Assert.ThrowsAsync<Cp4Refusal>(() => CreateAsync(expected));
        Assert.Contains("system identifier", e.Message);
        AssertNoWrite();
    }

    [Fact]
    public async Task Refuses_a_server_that_is_not_postgresql_17()
    {
        cluster.Identity = cluster.Identity with { ServerVersionNum = 160004 };
        await Assert.ThrowsAsync<Cp4Refusal>(() => CreateAsync());
        AssertNoWrite();
    }

    [Fact]
    public async Task Refuses_a_connection_that_is_not_the_cluster_superuser()
    {
        cluster.Identity = cluster.Identity with { IsSuperuser = false };
        await Assert.ThrowsAsync<Cp4Refusal>(() => CreateAsync());
        AssertNoWrite();
    }

    [Fact]
    public async Task A_missing_application_role_is_refused_never_created()
    {
        cluster.Roles.RemoveAll(r => r.Name == "accounts_app");
        var e = await Assert.ThrowsAsync<Cp4Refusal>(() => CreateAsync());
        Assert.Contains("accounts_app", e.Message);
        AssertNoWrite();
    }

    [Fact]
    public async Task An_application_role_with_elevated_attributes_is_refused_never_altered()
    {
        cluster.Roles[0] = cluster.Roles[0] with { CreateRole = true };
        await Assert.ThrowsAsync<Cp4Refusal>(() => CreateAsync());
        AssertNoWrite();
    }

    [Fact]
    public async Task An_existing_database_with_its_name_is_refused()
    {
        cluster.Databases["bg_t_20261023120000_cp4_0123abcd"] = 99;
        await Assert.ThrowsAsync<Cp4Refusal>(() => CreateAsync());
        AssertNoWrite();
        Assert.Equal(99u, cluster.Databases["bg_t_20261023120000_cp4_0123abcd"]);
    }

    [Fact]
    public async Task A_record_left_by_an_earlier_run_is_refused_instead_of_repaired()
    {
        File.WriteAllText(Record, "{}");
        var e = await Assert.ThrowsAsync<Cp4Refusal>(() => CreateAsync());
        Assert.Contains("earlier", e.Message);
        Assert.Empty(cluster.Calls);
    }

    [Fact]
    public async Task Other_tests_databases_are_never_touched_however_old()
    {
        cluster.Databases["bg_t_20200101000000_deadbeef"] = 41; // a stale database of another run: TestCluster would drop it
        var record = await CreateAsync();
        await Cp4Database.DropAsync(cluster, SystemId, Record);
        Assert.Equal(41u, cluster.Databases["bg_t_20200101000000_deadbeef"]);
        Assert.DoesNotContain(cluster.Calls, c => c.Contains("deadbeef", StringComparison.Ordinal));
        Assert.False(cluster.Databases.ContainsKey(record.Name));
    }

    [Fact]
    public async Task Drop_drops_only_the_recorded_database_when_its_oid_still_matches()
    {
        var record = await CreateAsync();
        cluster.Calls.Clear();

        Assert.True(await Cp4Database.DropAsync(cluster, SystemId, Record));

        Assert.Equal(["identity", $"oid {record.Name}", $"drop {record.Name}", $"oid {record.Name}"], cluster.Calls);
        Assert.False(File.Exists(Record));
    }

    [Fact]
    public async Task Drop_refuses_when_the_name_now_belongs_to_another_database()
    {
        var record = await CreateAsync();
        cluster.Databases[record.Name] = record.Oid + 1; // dropped and recreated by someone else
        cluster.Calls.Clear();

        await Assert.ThrowsAsync<Cp4Refusal>(() => Cp4Database.DropAsync(cluster, SystemId, Record));

        AssertNoWrite();
        Assert.True(File.Exists(Record), "the record is kept for the operator");
    }

    [Fact]
    public async Task Drop_of_an_already_absent_database_drops_nothing_and_clears_the_record()
    {
        var record = await CreateAsync();
        cluster.Databases.Remove(record.Name);
        cluster.Calls.Clear();

        Assert.False(await Cp4Database.DropAsync(cluster, SystemId, Record));

        AssertNoWrite();
        Assert.False(File.Exists(Record));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("bg_t_20261023120000_0123abcd")] // an ordinary test database, not CP4's
    [InlineData("bg_t_20261023120000_cp4_0123abcd\"; drop database postgres; --")]
    public async Task Drop_refuses_a_record_that_names_anything_but_a_cp4_database(string name)
    {
        cluster.Databases[name] = 7;
        Cp4Database.WriteRecord(Record, new Cp4DatabaseRecord(name, 7, SystemId, Now));

        await Assert.ThrowsAsync<Cp4Refusal>(() => Cp4Database.DropAsync(cluster, SystemId, Record));

        AssertNoWrite();
    }

    [Fact]
    public async Task Drop_refuses_a_record_made_on_another_cluster()
    {
        await CreateAsync();
        cluster.Identity = cluster.Identity with { SystemIdentifier = "2222222222222222222" };
        cluster.Calls.Clear();

        await Assert.ThrowsAsync<Cp4Refusal>(() => Cp4Database.DropAsync(cluster, "2222222222222222222", Record));

        AssertNoWrite();
    }

    [Fact]
    public void The_baseline_comparison_names_every_change_and_nothing_else()
    {
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var before = new Cp4Baseline(start, [new("postgres", 5, 700), new("bg_t_x", 16384, 800)], [new("accounts_app", 10, 600), new("postgres", 11, 1)]);

        Assert.Empty(Cp4Database.Differences(before, before with { }));
        var after = new Cp4Baseline(start.AddMinutes(1), [new("postgres", 5, 700), new("bg_t_new", 16390, 900)], [new("accounts_app", 10, 650), new("postgres", 11, 1)]);
        Assert.Equal(["postmaster restarted", "database added: bg_t_new (oid 16390)", "database removed: bg_t_x (oid 16384)", "role changed: accounts_app (xmin 600 -> 650)"],
            Cp4Database.Differences(before, after));
    }

    [Fact]
    public void The_cp4_path_issues_no_role_statement_and_no_cluster_wide_cleanup()
    {
        var source = File.ReadAllText(Cp4Database.SourcePath());
        foreach (var banned in new[] { "create role", "alter role", "drop role", "grant ", "revoke ", "bg_test_outsider", "TestCluster.Instance", "CreateDatabaseAsync()", "like 'bg" })
            Assert.DoesNotContain(banned, source, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOf(source, "drop database"));
        Assert.Equal(1, CountOf(source, "create database"));
    }

    static int CountOf(string text, string what)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(what, at, StringComparison.OrdinalIgnoreCase)) >= 0) { count++; at += what.Length; }
        return count;
    }

    sealed class FakeCluster : ICp4ClusterAdmin
    {
        public List<string> Calls { get; } = [];
        public Cp4ClusterIdentity Identity { get; set; } = new("blackglass-test", 170004, SystemId, "postgres", true);
        public List<Cp4Role> Roles { get; } = [new("accounts_app", false, false, false), new("accounts_migrator", false, false, false)];
        public Dictionary<string, uint> Databases { get; } = new() { ["postgres"] = 5 };
        uint next = 16384;

        public Task<Cp4ClusterIdentity> IdentityAsync() { Calls.Add("identity"); return Task.FromResult(Identity); }

        public Task<IReadOnlyList<Cp4Role>> RolesAsync(IReadOnlyList<string> names)
        {
            Calls.Add("roles");
            return Task.FromResult<IReadOnlyList<Cp4Role>>(Roles.Where(r => names.Contains(r.Name)).ToList());
        }

        public Task<uint?> DatabaseOidAsync(string name)
        {
            Calls.Add($"oid {name}");
            return Task.FromResult(Databases.TryGetValue(name, out var oid) ? oid : (uint?)null);
        }

        public Task CreateDatabaseAsync(string name, string owner)
        {
            Calls.Add($"create {name} owner {owner}");
            Databases.Add(name, next++);
            return Task.CompletedTask;
        }

        public Task DropDatabaseAsync(string name)
        {
            Calls.Add($"drop {name}");
            Databases.Remove(name);
            return Task.CompletedTask;
        }

        public Task<Cp4Baseline> BaselineAsync() => throw new NotSupportedException("not used by the lifecycle");
    }
}
