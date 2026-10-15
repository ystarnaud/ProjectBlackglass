using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.Domain.Auth;

namespace Blackglass.Accounts.IntegrationTests.Unit;

/// <summary>Review B, Important 1: once the global login window is spent, fabricated keys must not keep creating
/// per-key limiter partitions (each lives about a minute), or a flood of invented keys grows memory without bound.</summary>
public class LoginThrottleTests
{
    static AuthSettings Settings(int perKey, int global) => AuthSettings.FromEnvironment(new Dictionary<string, string?>
    {
        [AuthSettings.LoginRateVar] = perKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [AuthSettings.LoginGlobalRateVar] = global.ToString(System.Globalization.CultureInfo.InvariantCulture),
    });

    [Fact]
    public void Fabricated_keys_create_no_partitions_once_the_global_window_is_spent()
    {
        using var throttle = new LoginThrottle(Settings(perKey: 10, global: 3));
        for (var i = 0; i < 3; i++) Assert.True(throttle.TryAcquire(OpaqueSecret.New("dk1").Hash()));
        var before = throttle.PartitionsCreated;
        for (var i = 0; i < 1000; i++) Assert.False(throttle.TryAcquire(OpaqueSecret.New("dk1").Hash()));
        Assert.Equal(before, throttle.PartitionsCreated);
    }

    [Fact]
    public void A_repeated_key_is_stopped_by_its_own_window_without_spending_the_global_one()
    {
        using var throttle = new LoginThrottle(Settings(perKey: 2, global: 5));
        var hammered = OpaqueSecret.New("dk1").Hash();
        for (var i = 0; i < 20; i++) throttle.TryAcquire(hammered);
        for (var i = 0; i < 3; i++) Assert.True(throttle.TryAcquire(OpaqueSecret.New("dk1").Hash())); // 5 - 2 global permits left
        Assert.False(throttle.TryAcquire(OpaqueSecret.New("dk1").Hash()));
    }
}
