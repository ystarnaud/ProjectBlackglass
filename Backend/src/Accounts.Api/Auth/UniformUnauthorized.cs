namespace Blackglass.Accounts.Api.Auth;

/// <summary>
/// The one 401 every authentication failure gets, whatever the cause (unknown, malformed, expired or revoked token or
/// key, suspended account): no body and a bare "Bearer" challenge, so a response never tells the caller which it was.
/// </summary>
public static class UniformUnauthorized
{
    public static void Apply(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.WWWAuthenticate = "Bearer";
    }

    public static IResult Result { get; } = new UnauthorizedResult();

    sealed class UnauthorizedResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            Apply(httpContext.Response);
            return Task.CompletedTask;
        }
    }
}
