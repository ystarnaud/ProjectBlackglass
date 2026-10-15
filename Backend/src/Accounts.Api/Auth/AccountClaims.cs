using System.Globalization;
using System.Security.Claims;

namespace Blackglass.Accounts.Api.Auth;

/// <summary>The authenticated account and session: the only source of an account id for any client endpoint (spec
/// §4.2). Claims hold the decimal id internally; it reaches the wire only through AccountIdJsonConverter.</summary>
public static class AccountClaims
{
    public const string Scheme = "Bearer";
    public const string AccountId = "sub";
    public const string SessionId = "sid";

    public static Claim[] For(long accountId, Guid sessionId) =>
        [new Claim(AccountId, accountId.ToString(CultureInfo.InvariantCulture)), new Claim(SessionId, sessionId.ToString())];

    public static long AccountOf(ClaimsPrincipal user) => long.Parse(user.FindFirstValue(AccountId)!, NumberStyles.None, CultureInfo.InvariantCulture);

    public static Guid SessionOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(SessionId)!);
}
