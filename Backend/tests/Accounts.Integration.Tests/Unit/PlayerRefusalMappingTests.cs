using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.Api.Player;
using Blackglass.Accounts.Persistence.Player;

namespace Blackglass.Accounts.IntegrationTests.Unit;

/// <summary>
/// Owner ruling S5-D5: InventoryLocked is 409 on every route. The mapping from PlayerError to a status is a switch with no
/// default arm, so a new PlayerError member fails the build until it is mapped; these tests also iterate every member.
/// </summary>
public class PlayerRefusalMappingTests
{
    static readonly Dictionary<PlayerError, int> Expected = new()
    {
        [PlayerError.InventoryLocked] = 409,
        [PlayerError.VersionMismatch] = 409,
        [PlayerError.NotProvisioned] = 409,
        [PlayerError.OperativeNotFound] = 404,
        [PlayerError.NoPendingPick] = 422,
        [PlayerError.UnknownChoice] = 422,
    };

    [Fact]
    public void Every_PlayerError_maps_and_InventoryLocked_is_409()
    {
        var all = Enum.GetValues<PlayerError>();
        Assert.Equal(all.Order(), Expected.Keys.Order()); // a new member must be given its expected status here too
        foreach (var error in all) Assert.Equal(Expected[error], PlayerEndpoints.StatusFor(error));
        Assert.Equal(409, PlayerEndpoints.StatusFor(PlayerError.InventoryLocked));
    }

    [Fact]
    public void An_invariant_failure_and_a_checked_overflow_are_both_server_faults_and_nothing_else_is()
    {
        // An overflow needs a stack above long.MaxValue units, which no request can build, so the decision is tested directly.
        Assert.True(PlayerEndpoints.IsInventoryServerFault(new InventoryInvariantException("broken")));
        Assert.True(PlayerEndpoints.IsInventoryServerFault(new OverflowException()));
        Assert.False(PlayerEndpoints.IsInventoryServerFault(new InvalidOperationException()));
        Assert.False(PlayerEndpoints.IsInventoryServerFault(new OperationCanceledException()));
    }

    [Fact]
    public void An_undefined_PlayerError_is_not_silently_mapped()
    {
        Assert.Throws<System.Runtime.CompilerServices.SwitchExpressionException>(() => PlayerEndpoints.StatusFor((PlayerError)999));
    }

    [Fact]
    public void The_mapping_switch_has_no_default_arm()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "source", "Accounts.Api", "Player", "PlayerEndpoints.cs");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("static int StatusFor(", StringComparison.Ordinal);
        Assert.True(start >= 0, "StatusFor not found");
        var end = source.IndexOf("};", start, StringComparison.Ordinal);
        var body = source[start..end];
        Assert.DoesNotContain("_ =>", body);
        Assert.DoesNotContain("default", body);
    }
}
