using Blackglass.Accounts.IntegrationTests.Support;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class TestClusterGuardTests
{
    [Fact]
    public void Accepts_only_the_test_cluster()
    {
        TestCluster.EnsureTestCluster("blackglass-test");
        Assert.Throws<InvalidOperationException>(() => TestCluster.EnsureTestCluster("blackglass-accounts"));
        Assert.Throws<InvalidOperationException>(() => TestCluster.EnsureTestCluster(""));
    }
}
