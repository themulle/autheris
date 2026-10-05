namespace Autheris.Api.Endpoints;

using System.Linq;
using System.Security.Claims;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/login", (ClaimsPrincipal principal) =>
        {
            var sid = principal.GetUserSid()?.Value;
            var name = principal.Identity?.Name ?? sid;
            var roles = principal.GetUserRoles().ToList();
            var groups = principal.GetGroupSids().Select(g => g.Value).ToList();

            return Results.Ok(new
            {
                authenticated = true,
                user = name,
                sid = sid,
                roles = roles,
                groups = groups,
                authenticationType = principal.Identity?.AuthenticationType ?? "Basic"
            });
        }).RequireAuthorization();

        app.MapPost("/api/auth/login", (ClaimsPrincipal principal) =>
        {
            var sid = principal.GetUserSid()?.Value;
            var name = principal.Identity?.Name ?? sid;
            var roles = principal.GetUserRoles().ToList();
            var groups = principal.GetGroupSids().Select(g => g.Value).ToList();

            return Results.Ok(new
            {
                authenticated = true,
                user = name,
                sid = sid,
                roles = roles,
                groups = groups,
                authenticationType = principal.Identity?.AuthenticationType ?? "Basic"
            });
        }).RequireAuthorization();

        // F-AUTH-DX: effective identity of the current request (works for every scheme, incl. the session cookie).
        app.MapGet("/api/auth/session", GetSession).RequireAuthorization();

        // F-AUTH-DX: ends the Basic session: revokes its id and deletes the cookie. Anonymous so that an expired
        // cookie can always be cleared. Browsers that cached Basic credentials from the native dialog will send
        // them again on the next 401; use a private window to switch users in the browser.
        app.MapPost("/api/auth/logout", (Delegate)LogoutAsync).AllowAnonymous();

        return app;
    }

    internal static IResult GetSession(HttpContext context)
    {
        var principal = context.User;
        var authenticateResult = context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult;
        var isSession = BasicAuthSession.IsSessionPrincipal(principal);

        return Results.Ok(new
        {
            authenticated = true,
            user = principal.Identity?.Name,
            sid = principal.GetUserSid()?.Value,
            tenant = principal.FindFirst("tenant_id")?.Value,
            roles = principal.GetUserRoles().ToList(),
            groups = principal.GetGroupSids().Select(g => g.Value).ToList(),
            authenticationType = principal.Identity?.AuthenticationType,
            authMethod = principal.FindFirst(BasicAuthSession.AuthMethodClaimType)?.Value,
            session = isSession
                ? new
                {
                    id = principal.FindFirst(BasicAuthSession.JtiClaimType)?.Value,
                    issuedAt = BasicAuthSession.GetIssuedAt(principal),
                    expiresAt = authenticateResult?.Properties?.ExpiresUtc
                }
                : null
        });
    }

    internal static async Task<IResult> LogoutAsync(HttpContext context)
    {
        var revoked = false;
        if (BasicAuthSession.IsSessionPrincipal(context.User) &&
            context.User.FindFirst(BasicAuthSession.JtiClaimType)?.Value is { Length: > 0 } jti)
        {
            var revocationService = context.RequestServices.GetService<ITokenRevocationService>();
            var options = context.RequestServices.GetService<IOptions<GatewayOptions>>()?.Value;
            if (revocationService != null && options != null)
            {
                var issuedAt = BasicAuthSession.GetIssuedAt(context.User) ?? DateTimeOffset.UtcNow;
                var until = issuedAt.AddMinutes(options.Authentication.BasicAuth.Session.AbsoluteExpirationMinutes);
                await revocationService.RevokeAsync(jti, until, context.RequestAborted).ConfigureAwait(false);
                revoked = true;
            }
        }

        var schemes = context.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
        if (await schemes.GetSchemeAsync(BasicAuthSession.SchemeName).ConfigureAwait(false) != null)
        {
            await context.SignOutAsync(BasicAuthSession.SchemeName).ConfigureAwait(false);
        }

        return Results.Ok(new { loggedOut = true, sessionRevoked = revoked });
    }
}
