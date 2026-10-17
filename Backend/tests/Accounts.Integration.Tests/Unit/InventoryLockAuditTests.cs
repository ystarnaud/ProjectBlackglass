using System.Reflection;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Persistence.Player;

namespace Blackglass.Accounts.IntegrationTests.Unit;

/// <summary>
/// The inventory lock is mandatory and has no unlocked fallback (owner ruling S7-D4): ParticipationLock is the only
/// implementation in the production assemblies, and each store that asks it requires one.
/// </summary>
public class InventoryLockAuditTests
{
    static readonly Assembly[] Production = [typeof(ServeCommand).Assembly, typeof(ParticipationLock).Assembly, typeof(GameCatalogue).Assembly];

    [Fact]
    public void No_production_type_implements_an_unlocked_fallback()
    {
        Assert.Equal(3, Production.Select(a => a.GetName().Name).Distinct().Count()); // Api, Persistence and Domain
        var implementations = Production.SelectMany(a => a.GetTypes())
            .Where(t => t != typeof(IInventoryLock) && typeof(IInventoryLock).IsAssignableFrom(t)).ToList();
        Assert.Equal([typeof(ParticipationLock)], implementations);
        Assert.True(typeof(ParticipationLock).IsSealed);

        // Each store has one constructor, and its lock parameter is required and non-nullable: no default to fall back on.
        var nullability = new NullabilityInfoContext();
        foreach (var store in new[] { typeof(PickStore), typeof(InventoryEditStore), typeof(PlayerReadStore) })
        {
            var parameter = Assert.Single(Assert.Single(store.GetConstructors()).GetParameters(), p => p.ParameterType == typeof(IInventoryLock));
            Assert.False(parameter.IsOptional, store.Name);
            Assert.False(parameter.HasDefaultValue, store.Name);
            Assert.Equal(NullabilityState.NotNull, nullability.Create(parameter).WriteState);
        }

        // The Stage 5 always-unlocked class is gone from the source too.
        var sources = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "source"), "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(sources);
        Assert.DoesNotContain(sources, f => File.ReadAllText(f).Contains("NoParticipationsYet", StringComparison.Ordinal));
    }
}
