namespace Autheris.Api.Endpoints;

using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Control-plane report of what the authorization migration revoked or deactivated: consents of role grantees and access-profile
/// assignments, across tenants, for the canonical ClusterAdmin only. Relationship tuples are not exported (none were ever deployed)
/// and there is no subquery-filter section.
/// </summary>
public static class AuthzMigrationReportEndpoints
{
    public static IEndpointRouteBuilder MapAuthzMigrationReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/admin/authz/migration-report", (HttpContext context, IAccessProfileRepository repository, CancellationToken ct) =>
                GetAsync(context, repository, ct))
            .RequireAuthorization(GatewayPolicies.ClusterAdmin)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        return app;
    }

    internal static async Task<IResult> GetAsync(HttpContext context, IAccessProfileRepository repository, CancellationToken ct)
    {
        if (!EndpointSecurity.IsCanonicalClusterAdmin(context.User))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var consents = await repository.GetRevokedRoleConsentsAsync(ct).ConfigureAwait(false);
        var assignments = await repository.GetDeactivatedAssignmentsAsync(ct).ConfigureAwait(false);
        return Results.Ok(new { revokedRoleConsents = consents, deactivatedProfileAssignments = assignments });
    }
}
