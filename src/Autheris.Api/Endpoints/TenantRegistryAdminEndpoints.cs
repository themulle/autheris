namespace Autheris.Api.Endpoints;

using System.Text.Json;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// M-A2: the operator path out of a tenant id collision (CR-ADG-31). A collided spelling group denies every spelling until a canonical
/// ClusterAdmin names the spelling to keep; the operation is audited (<see cref="AuditEventTypes.TenantCollisionResolved"/>). It only changes
/// a group that carries the collision mark, so it cannot rename a healthy tenant.
/// </summary>
public static class TenantRegistryAdminEndpoints
{
    public static IEndpointRouteBuilder MapTenantRegistryAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/tenant-registry/{tenantId}/resolve-collision", (
            string tenantId,
            HttpContext context,
            ITenantRegistry registry,
            IAuditLogRepository audit,
            CancellationToken ct) => ResolveCollisionAsync(tenantId, context, registry, audit, ct))
            .RequireAuthorization(GatewayPolicies.ClusterAdmin)
            .WithAudit(AuditLevel.Delegated, AuditEventTypes.TenantCollisionResolved);

        return app;
    }

    /// <param name="tenantId">The exact spelling to keep as the tenant of its group.</param>
    internal static async Task<IResult> ResolveCollisionAsync(
        string tenantId, HttpContext context, ITenantRegistry registry, IAuditLogRepository audit, CancellationToken ct)
    {
        if (!EndpointSecurity.IsCanonicalClusterAdmin(context.User))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!TenantId.TryParse(tenantId, out var keep))
        {
            return Results.BadRequest(new { error = "tenantId is not a valid tenant id." });
        }

        TenantId actorTenant;
        try
        {
            actorTenant = context.User.GetTenantId();
        }
        catch (System.Security.SecurityException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!await registry.ResolveCollisionAsync(keep, ct).ConfigureAwait(false))
        {
            return Results.NotFound(new { error = "No collided tenant group exists for this tenant id." });
        }

        await audit.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = actorTenant,
            EventType = AuditEventTypes.TenantCollisionResolved,
            ActorSid = context.User.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = "ALLOW",
            TraceId = context.TraceIdentifier,
            DetailsJson = JsonSerializer.Serialize(new { keptSpelling = keep.Value, group = TenantRegistryKey.Normalize(keep.Value) })
        }, ct).ConfigureAwait(false);
        context.Features.Get<AuditContext>()?.MarkHandled();

        return Results.Ok(new { resolved = keep.Value, note = "Every other spelling of this tenant id stays denied." });
    }
}
