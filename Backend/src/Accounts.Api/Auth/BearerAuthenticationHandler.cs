using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Persistence.Accounts;

namespace Blackglass.Accounts.Api.Auth;

/// <summary>
/// "Authorization: Bearer at1_…" → the session's account (spec §4.2). Exactly one header, the exact scheme and one space,
/// a strictly parsed token, then one database check per request (no cache, so revocation is immediate). Failures are the
/// uniform 401; the log names the reason and at most an 8-hex hash prefix. A database failure is not a bad token: the
/// NpgsqlException propagates to AuthEndpoints.UseDatabaseFailureResponses (503 unavailable or busy, 500 otherwise).
/// </summary>
public sealed class BearerAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SessionStore sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    const string Prefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var values = Request.Headers.Authorization;
        if (values.Count == 0) return AuthenticateResult.NoResult();
        if (values.Count != 1 || values[0] is not { } header || !header.StartsWith(Prefix, StringComparison.Ordinal))
            return Fail("malformed authorization header");
        if (!OpaqueSecret.TryParse(header[Prefix.Length..], OpaqueSecret.AccessTokenPrefix, out var token))
            return Fail("malformed token");
        var hash = token!.Hash();
        var session = await sessions.AuthenticateAsync(hash, Context.RequestAborted);
        if (session is null) return Fail($"unknown, expired or revoked token {OpaqueSecret.HashPrefix(hash)}");
        var identity = new ClaimsIdentity(AccountClaims.For(session.AccountId, session.SessionId), AccountClaims.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AccountClaims.Scheme));
    }

    AuthenticateResult Fail(string reason)
    {
        Logger.LogInformation("Authentication failed: {Reason}", reason);
        return AuthenticateResult.Fail(reason);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        UniformUnauthorized.Apply(Response);
        return Task.CompletedTask;
    }
}
