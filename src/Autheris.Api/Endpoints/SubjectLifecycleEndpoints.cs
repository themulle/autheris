namespace Autheris.Api.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Identity.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Autheris.Api.Extensions;
using Autheris.Domain.Audit;

public static class SubjectLifecycleEndpoints
{
    public sealed record DeprovisionRequest(string UserKey, string RealmId, string Reason);

    public static IEndpointRouteBuilder MapSubjectLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenants/{tenantId}/lifecycle")
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin);

        group.MapPost("/deprovision", async (
            string tenantId,
            DeprovisionRequest request,
            HttpContext httpContext,
            IIdentityDeprovisioningService deprovisioningService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request?.UserKey) || string.IsNullOrWhiteSpace(request?.RealmId))
            {
                return Results.BadRequest(new { error = "userKey and realmId are required." });
            }

            var initiatorSid = httpContext.User.GetUserSid() ?? new Sid("ADMIN");
            await deprovisioningService.DeprovisionAsync(
                new TenantId(tenantId),
                request.RealmId,
                request.UserKey,
                request.Reason ?? "Administrative deprovisioning",
                initiatorSid,
                ct).ConfigureAwait(false);

            return Results.Ok(new { message = $"User {request.UserKey} successfully locked and deprovisioned in realm {request.RealmId}." });
        }).WithAudit(AuditLevel.Full, AuditEventTypes.TokenRevoked);

        group.MapGet("/locks/{realmId}/{userKey}", async (
            string tenantId,
            string realmId,
            string userKey,
            IIdentityStoreRepository identityStore,
            CancellationToken ct) =>
        {
            var isLocked = await identityStore.IsSubjectLockedAsync(new TenantId(tenantId), realmId, userKey, ct).ConfigureAwait(false);
            return Results.Ok(new { userKey, realmId, isLocked });
        }).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapDelete("/locks/{realmId}/{userKey}", async (
            string tenantId,
            string realmId,
            string userKey,
            IIdentityStoreRepository identityStore,
            CancellationToken ct) =>
        {
            await identityStore.RemoveLockAsync(new TenantId(tenantId), realmId, userKey, ct).ConfigureAwait(false);
            return Results.NoContent();
        }).WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }
}
