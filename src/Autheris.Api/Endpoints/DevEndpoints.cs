namespace Autheris.Api.Endpoints;

using System.Linq;
using Autheris.Api.Configuration;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// F-AUTH-DX: Development-only helper endpoints (persona list, one-click persona login, effective configuration).
/// Nothing is mapped outside Development, so the routes do not exist in any other environment.
/// </summary>
public static class DevEndpoints
{
    public sealed record Persona(string Username, string Sid, string Tenant, IReadOnlyList<string> Roles, string LoginUrl);

    public static IEndpointRouteBuilder MapDevEndpoints(this IEndpointRouteBuilder app, GatewayOptions options)
    {
        var env = app.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var features = DevFeatures.Resolve(options, env.IsDevelopment());
        var report = app.ServiceProvider.GetService<DevConfigurationReport>();

        var group = app.MapGroup("/api/dev").AllowAnonymous();
        if (features.Info)
        {
            group.MapGet("/info", () => Results.Ok(BuildInfo(options, env, report)));
        }

        if (features.PersonaLogin)
        {
            group.MapGet("/personas", () => Results.Ok(ListPersonas(options)));
            group.MapGet("/login/{persona}", (string persona, string? redirect, HttpContext context) =>
                LoginAsync(persona, redirect, context, options, env));
        }

        return app;
    }

    public static IReadOnlyList<Persona> ListPersonas(GatewayOptions options) =>
        options.Authentication.BasicAuth.Users
            .Select(u => new Persona(
                u.Username,
                BasicAuthPrincipalFactory.ResolveSid(u),
                BasicAuthPrincipalFactory.ResolveTenant(u),
                u.Roles.ToList(),
                $"/api/dev/login/{Uri.EscapeDataString(u.Username)}"))
            .ToList();

    internal static async Task<IResult> LoginAsync(
        string persona, string? redirect, HttpContext context, GatewayOptions options, IHostEnvironment env)
    {
        if (!BasicAuthSession.IsAllowed(options, env))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Session login unavailable",
                detail: $"BasicAuth.Session is not active: {BasicAuthSession.GetInactiveReason(options, env) ?? "not enabled"}.");
        }

        var user = BasicAuthPrincipalFactory.FindUser(options.Authentication.BasicAuth, persona);
        if (user == null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Unknown persona",
                detail: $"Available personas: {string.Join(", ", options.Authentication.BasicAuth.Users.Select(u => u.Username))}.");
        }

        await BasicAuthSession.IssueAsync(context, user).ConfigureAwait(false);

        return IsLocalPath(redirect)
            ? Results.Redirect(redirect!)
            : Results.Ok(new { loggedIn = true, user = user.Username, sid = BasicAuthPrincipalFactory.ResolveSid(user) });
    }

    /// <summary>Only same-site absolute paths (<c>/x</c>) are valid redirect targets (no open redirect).</summary>
    internal static bool IsLocalPath(string? target) =>
        !string.IsNullOrEmpty(target) &&
        target[0] == '/' &&
        (target.Length == 1 || (target[1] != '/' && target[1] != '\\')) &&
        !target.Contains('\r') && !target.Contains('\n');

    /// <summary>Effective, secret-free configuration summary. Connection strings and passwords are never included.</summary>
    internal static object BuildInfo(GatewayOptions options, IHostEnvironment env, DevConfigurationReport? report = null)
    {
        var auth = options.Authentication;
        var features = DevFeatures.Resolve(options, env.IsDevelopment());
        return new
        {
            environment = env.EnvironmentName,
            dev = new
            {
                preset = options.Dev.Preset,
                features = new { features.Banner, features.VerboseErrors, features.PersonaLogin, features.Info, features.PersistDb },
                devSecurity = features.DevSecurity,
                applied = report?.Applied ?? new Dictionary<string, string>(),
                notes = report?.Notes ?? []
            },
            auth = new
            {
                basic = auth.BasicAuth.Enabled,
                basicUsers = auth.BasicAuth.Users.Count,
                realm = auth.BasicAuth.Realm,
                sessionActive = BasicAuthSession.IsAllowed(options, env),
                sessionInactiveReason = BasicAuthSession.GetInactiveReason(options, env),
                testAuthHandler = auth.EnableTestAuthHandler,
                forwardAuth = auth.ForwardAuth.Enabled,
                entraId = auth.EntraId.Enabled,
                adfs = auth.Adfs.Enabled,
                kerberosOnly = auth.RequireKerberosOnly
            },
            governanceDb = new { provider = options.GovernanceDb.Provider, seedDemoData = options.GovernanceDb.SeedDemoData },
            rateLimiting = new
            {
                disabled = options.IsRateLimitingDisabled,
                preAuthPermitLimit = options.RateLimiting.PreAuthIpRateLimit.PermitLimit,
                preAuthWindowSeconds = options.RateLimiting.PreAuthIpRateLimit.WindowSeconds
            },
            graphql = new
            {
                endpoint = options.GraphQL.EndpointPath,
                introspection = options.GraphQL.EnableIntrospection,
                bananaCakePop = options.GraphQL.EnableBananaCakePop,
                maxDepth = options.GraphQL.MaxAllowedExecutionDepth
            },
            insecureBypasses = options.GetAllActiveBypasses(),
            personas = ListPersonas(options)
        };
    }
}
