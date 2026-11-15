namespace Autheris.Api.Endpoints;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Audit;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

/// <summary>Effective-access report endpoint for a subject across matching catalog objects.</summary>
public static class EffectiveAccessEndpoints
{
    public static IEndpointRouteBuilder MapEffectiveAccessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/authz/subjects/{subject}/effective-access", GetEffectiveAccessAsync)
            .WithName("AuthzSubjectEffectiveAccess")
            .RequireAuthorization()
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithAudit(AuditLevel.Delegated, AuditEventTypes.AuthzEffectiveAccessReport);
        return app;
    }

    internal static async Task<IResult> GetEffectiveAccessAsync(
        string subject,
        [FromQuery(Name = "object")] string? objectPattern,
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        HttpContext context,
        IEffectiveAccessService service,
        CancellationToken ct)
    {
        var query = new EffectiveAccessReportQuery(subject, objectPattern, cursor, pageSize);
        var result = await service.GetEffectiveAccessAsync(context.GetSecurityContext(), query, ct).ConfigureAwait(false);
        return ToResult(result.Outcome, result.Report, context);
    }

    internal static IResult ToResult(EffectiveAccessOutcome outcome, object? payload, HttpContext context) =>
        AuthorizationExplainEndpoints.ToResult((ExplainOutcome)outcome, payload, context);
}
