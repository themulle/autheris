using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Middleware;

/// <summary>
/// F-AUTH-DX: Issues the session cookie after a successful Basic login, so subsequent requests (browser, cookie
/// jars, WebSocket, EventSource) authenticate without re-sending and re-verifying the password.
/// Runs after authentication and token revocation; a cookie is never issued for a revoked identity.
/// </summary>
public sealed class BasicAuthSessionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GatewayOptions _options;
    private readonly bool _enabled;
    private readonly ILogger<BasicAuthSessionMiddleware> _logger;

    public BasicAuthSessionMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        IHostEnvironment environment,
        ILogger<BasicAuthSessionMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _enabled = BasicAuthSession.IsAllowed(_options, environment);
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_enabled && await ShouldIssueAsync(context).ConfigureAwait(false))
        {
            var user = BasicAuthPrincipalFactory.FindUser(_options.Authentication.BasicAuth, context.User.Identity?.Name);
            if (user != null)
            {
                await BasicAuthSession.IssueAsync(context, user).ConfigureAwait(false);

                _logger.LogInformation(
                    "Basic session issued for {User} (tenant {Tenant}).",
                    user.Username,
                    BasicAuthPrincipalFactory.ResolveTenant(user));
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private async Task<bool> ShouldIssueAsync(HttpContext context)
    {
        // Only a fresh Basic header login creates a session (not cookie, Bearer, ForwardAuth, Negotiate).
        if (context.User.Identity is not { IsAuthenticated: true, AuthenticationType: GatewayAuthSchemes.Basic })
        {
            return false;
        }

        if (string.Equals(context.Request.Headers[BasicAuthSession.NoSessionHeader], "1", StringComparison.Ordinal) ||
            context.Request.Path.StartsWithSegments("/health") ||
            context.Response.HasStarted)
        {
            return false;
        }

        if (!context.Request.Cookies.ContainsKey(_options.Authentication.BasicAuth.Session.CookieName))
        {
            return true;
        }

        // The header won over the cookie in the scheme selector. Keep a valid cookie of the same user (no
        // Set-Cookie on every header-authenticated request); replace it on user switch or when it is invalid.
        var existing = await context.AuthenticateAsync(BasicAuthSession.SchemeName).ConfigureAwait(false);
        return !(existing.Succeeded &&
                 string.Equals(existing.Principal?.Identity?.Name, context.User.Identity.Name, StringComparison.OrdinalIgnoreCase));
    }
}
