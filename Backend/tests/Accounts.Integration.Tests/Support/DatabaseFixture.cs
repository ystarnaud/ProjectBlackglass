namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>A fresh database per test class (spec §18.1 layer 2).</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public TestDatabase Db { get; private set; } = null!;

    public async Task InitializeAsync() => Db = await (await TestCluster.Instance).CreateDatabaseAsync();
    public async Task DisposeAsync() => await Db.DisposeAsync();
}
