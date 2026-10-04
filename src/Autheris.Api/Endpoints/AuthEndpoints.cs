namespace Autheris.Api.Endpoints;

using System.Linq;
using System.Security.Claims;
using Autheris.Api.Middleware;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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

        return app;
    }
}
