namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>A fresh database per test class (spec §18.1 layer 2).</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    TestDatabase? db;

    public TestDatabase Db => db ?? throw new InvalidOperationException("The test database was not created (initialization failed).");

    public async Task InitializeAsync() => db = await (await TestCluster.Instance).CreateDatabaseAsync();

    public async Task DisposeAsync()
    {
        if (db is not null) await db.DisposeAsync();
    }
}
