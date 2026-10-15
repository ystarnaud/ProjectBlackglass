using System.Globalization;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Ids;

/// <summary>
/// Origin references of the starter grants (spec §4: provisioning is idempotent through a unique origin reference). The
/// reference is a deterministic text from the account and the catalogue ids, so provisioning the same account twice
/// produces the same references and a unique constraint turns the second run into a no-op. The account is written as the
/// plain decimal bigint after 'a', k as the plain decimal ordinal of the copy after 'n'. Ids are catalogue ids, so they
/// contain no ':' and the forms cannot collide.
/// </summary>
public static class StarterRef
{
    public static string Operative(long accountId, string operativeDefinitionId)
    {
        var account = Account(accountId);
        Id(operativeDefinitionId, nameof(operativeDefinitionId));
        return $"starter:a{account}:op:{operativeDefinitionId}";
    }

    public static string OperativeItem(long accountId, string operativeDefinitionId, string itemDefinitionId, int k)
    {
        var account = Account(accountId);
        Id(operativeDefinitionId, nameof(operativeDefinitionId));
        Id(itemDefinitionId, nameof(itemDefinitionId));
        var copy = K(k);
        return $"starter:a{account}:op:{operativeDefinitionId}:item:{itemDefinitionId}:n{copy}";
    }

    public static string StashItem(long accountId, string itemDefinitionId, int k)
    {
        var account = Account(accountId);
        Id(itemDefinitionId, nameof(itemDefinitionId));
        var copy = K(k);
        return $"starter:a{account}:stash:item:{itemDefinitionId}:n{copy}";
    }

    static string Account(long accountId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        return accountId.ToString(CultureInfo.InvariantCulture);
    }

    static void Id(string? id, string name)
    {
        if (!CanonicalCatalogueJson.IsWellFormedId(id))
            throw new ArgumentException("Not a well-formed catalogue id.", name);
    }

    static string K(int k)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        return k.ToString(CultureInfo.InvariantCulture);
    }
}
