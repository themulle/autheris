using System.Security.Claims;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Autheris.Api.Security;

/// <summary>
/// F-AUTH-DX: Development-only diagnostics. A bare 403 is replaced by a problem+json that names the caller's
/// identity and, where known, the roles the endpoint requires. Never registered outside Development.
/// </summary>
public static class DevDiagnostics
{
    public static async Task WriteForbiddenAsync(HttpContext context, string detail)
    {
        var user = context.User;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Forbidden (development diagnostics)",
            Detail = detail,
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4"
        };
        problem.Extensions["user"] = user.Identity?.Name;
        problem.Extensions["sid"] = user.GetUserSid()?.Value;
        problem.Extensions["tenant"] = user.FindFirst("tenant_id")?.Value;
        problem.Extensions["roles"] = user.GetUserRoles().ToList();
        problem.Extensions["traceId"] = context.TraceIdentifier;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}

/// <summary>Explains policy-based 403 responses (named role policies) in Development.</summary>
public sealed class DevAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Forbidden)
        {
            return _default.HandleAsync(next, context, policy, authorizeResult);
        }

        return DevDiagnostics.WriteForbiddenAsync(context, Describe(context, policy));
    }

    internal static string Describe(HttpContext context, AuthorizationPolicy policy)
    {
        var have = string.Join(", ", context.User.GetUserRoles());
        var haveText = have.Length == 0 ? "none" : have;

        var policyNames = context.GetEndpoint()?.Metadata
            .OfType<IAuthorizeData>()
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .ToList() ?? [];

        foreach (var name in policyNames)
        {
            if (GatewayPolicies.TryGetPolicyRoles(name!, out var required))
            {
                return $"Policy '{name}' requires one of the roles [{string.Join(", ", required)}]. Caller roles: [{haveText}].";
            }
        }

        var requirements = string.Join(", ", policy.Requirements.Select(r => r.GetType().Name));
        return $"Authorization failed for requirements [{requirements}]. Caller roles: [{haveText}].";
    }
}

/// <summary>
/// Explains 403 responses that endpoint code returns without a body (<c>Results.StatusCode(403)</c>), which is
/// the common pattern in the feature endpoints. Responses that already carry content are left untouched.
/// </summary>
public sealed class DevForbiddenDiagnosticsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context).ConfigureAwait(false);

        if (context.Response.StatusCode == StatusCodes.Status403Forbidden &&
            !context.Response.HasStarted &&
            context.Response.ContentLength is null or 0 &&
            string.IsNullOrEmpty(context.Response.ContentType))
        {
            await DevDiagnostics.WriteForbiddenAsync(
                context,
                $"{context.Request.Method} {context.Request.Path} returned 403 without a reason. " +
                "Check the role, tenant and ownership checks of this endpoint against the caller identity below.")
                .ConfigureAwait(false);
        }
    }
}
