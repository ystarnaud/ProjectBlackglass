using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Hosts;

namespace Blackglass.Accounts.Api.Auth;

/// <summary>
/// The two mission principals (owner ruling B3, spec §4.3): a host (scheme <see cref="HostScheme"/>, the agent routes) and an
/// attempt's process (scheme <see cref="ProcessScheme"/>, the process routes). A route group requires exactly its policy, so an
/// account token, a host secret and a process credential each open only their own routes. Claims carry the host id or the
/// attempt id; no request body names its own principal.
/// </summary>
public static class MissionClaims
{
    public const string HostScheme = "Host";
    public const string ProcessScheme = "Process";
    public const string HostPolicy = "host";
    public const string ProcessPolicy = "process";
    public const string HostId = "host";
    public const string AttemptId = "attempt";

    public static string HostOf(ClaimsPrincipal user) => user.FindFirstValue(HostId)!;

    public static Guid AttemptOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(AttemptId)!);

    public static void AddMissionPolicies(this Microsoft.AspNetCore.Authorization.AuthorizationOptions o)
    {
        o.AddPolicy(HostPolicy, p => p.AddAuthenticationSchemes(HostScheme).RequireAuthenticatedUser());
        o.AddPolicy(ProcessPolicy, p => p.AddAuthenticationSchemes(ProcessScheme).RequireAuthenticatedUser());
    }
}

/// <summary>
/// The shared shape of the two mission schemes, like <see cref="BearerAuthenticationHandler"/>: exactly one Authorization header,
/// "Bearer " and a token parsed strictly with the scheme's own prefix, then one database lookup per request (no cache). Any
/// failure is the uniform 401; the log names the reason and at most an 8-hex hash prefix. A database failure propagates to the
/// database failure middleware (503 or 500), never a 401.
/// </summary>
public abstract class MissionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    const string Prefix = "Bearer ";

    protected abstract string SecretPrefix { get; }

    /// <summary>The principal's claims for a well-formed secret's hash, or the failure reason (never the secret or hash).</summary>
    protected abstract Task<(Claim[]? Claims, string? Failure)> LookupAsync(byte[] hash);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var values = Request.Headers.Authorization;
        if (values.Count == 0) return AuthenticateResult.NoResult();
        if (values.Count != 1 || values[0] is not { } header || !header.StartsWith(Prefix, StringComparison.Ordinal))
            return Fail("malformed authorization header");
        if (!OpaqueSecret.TryParse(header[Prefix.Length..], SecretPrefix, out var secret))
            return Fail("malformed credential");
        var hash = secret!.Hash();
        var (claims, failure) = await LookupAsync(hash);
        if (claims is null) return Fail($"{failure} {OpaqueSecret.HashPrefix(hash)}");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }

    AuthenticateResult Fail(string reason)
    {
        Logger.LogInformation("{Scheme} authentication failed: {Reason}", Scheme.Name, reason);
        return AuthenticateResult.Fail(reason);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        UniformUnauthorized.Apply(Response);
        return Task.CompletedTask;
    }
}

/// <summary>"Authorization: Bearer hs1_…" → the enabled agent host whose current secret it is.</summary>
public sealed class HostAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, HostStore hosts)
    : MissionAuthenticationHandler(options, logger, encoder)
{
    protected override string SecretPrefix => OpaqueSecret.HostSecretPrefix;

    protected override async Task<(Claim[]? Claims, string? Failure)> LookupAsync(byte[] hash) =>
        await hosts.AuthenticateAsync(hash, Context.RequestAborted) is { } host
            ? ([new Claim(MissionClaims.HostId, host.HostId)], null)
            : (null, "unknown, rotated or disabled host secret");
}

/// <summary>"Authorization: Bearer pc1_…" → the attempt whose current process credential it is, only while that attempt is
/// neither cancelled nor settled, and only on that attempt's routes: the route's {attemptId} (canonical lower case) must be
/// the credential's attempt, else the uniform 401.</summary>
public sealed class ProcessAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ProcessStore processes)
    : MissionAuthenticationHandler(options, logger, encoder)
{
    protected override string SecretPrefix => OpaqueSecret.ProcessCredentialPrefix;

    protected override async Task<(Claim[]? Claims, string? Failure)> LookupAsync(byte[] hash)
    {
        if (Request.RouteValues["attemptId"] is not string text || !CanonicalUuid.TryParse(text, out var routeAttempt))
            return (null, "no canonical attempt in the route");
        var process = await processes.AuthenticateAsync(hash, Context.RequestAborted);
        if (process is null) return (null, "unknown, superseded or ended process credential");
        if (process.AttemptId != routeAttempt) return (null, "process credential presented on another attempt's route");
        return ([new Claim(MissionClaims.AttemptId, process.AttemptId.ToString("D"))], null);
    }
}
