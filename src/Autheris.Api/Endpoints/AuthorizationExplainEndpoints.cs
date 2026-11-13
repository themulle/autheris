namespace Autheris.Api.Endpoints;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>Explain and grants listing; every call is audited by the service, denied ones included.</summary>
public static class AuthorizationExplainEndpoints
{
    public static IEndpointRouteBuilder MapAuthorizationExplainEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/authz/explain", ExplainAsync).WithName("AuthzExplain").RequireAuthorization()
            .WithAudit(AuditLevel.Delegated, AuditEventTypes.AuthzExplain);
        app.MapGet("/api/v1/authz/objects/{objectName}/grants", ListGrantsAsync).WithName("AuthzListGrants").RequireAuthorization()
            .WithAudit(AuditLevel.Delegated, AuditEventTypes.AuthzGrantsList);
        return app;
    }

    internal static async Task<IResult> ExplainAsync(ExplainRequest? request, HttpContext context, IAccessExplanationService service, CancellationToken ct)
    {
        if (request is null)
        {
            return ToResult(ExplainOutcome.BadRequest, null, context);
        }

        var result = await service.ExplainAsync(context.GetSecurityContext(), request, ct).ConfigureAwait(false);
        return ToResult(result.Outcome, result.Explanation, context);
    }

    internal static async Task<IResult> ListGrantsAsync(string objectName, HttpContext context, IAccessExplanationService service, CancellationToken ct)
    {
        var result = await service.ListGrantsAsync(context.GetSecurityContext(), objectName, ct).ConfigureAwait(false);
        return ToResult(result.Outcome, new { grants = result.Grants, truncated = result.Truncated }, context);
    }

    // Foreign-tenant, hidden and unknown objects share one body (no existence oracle).
    internal static IResult ToResult(ExplainOutcome outcome, object? payload, HttpContext context) => outcome switch
    {
        ExplainOutcome.Ok => Results.Ok(payload),
        ExplainOutcome.BadRequest => SafeErrorResults.BadRequest(ApiErrorCodes.BadRequest, "The request is missing or contains invalid fields.", context),
        ExplainOutcome.Forbidden => SafeErrorResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.CrossTenantAccessForbidden, "The caller may not explain access for this subject.", context),
        _ => SafeErrorResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.ResourceNotFound, "The requested resource was not found.", context)
    };
}
