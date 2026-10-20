namespace Autheris.Api.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Governance.Rights;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Autheris.Api.Extensions;
using Autheris.Domain.Audit;

public static class FunctionalRightEndpoints
{
    public sealed record ProposeMappingRequest(string GroupOrRole, string FunctionalRight, RecertClass? RecertClass);
    public sealed record ApproveMappingRequest(string GroupOrRole, string FunctionalRight);
    public sealed record DeleteMappingRequest(string GroupOrRole, string FunctionalRight);

    public static IEndpointRouteBuilder MapFunctionalRightEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenants/{tenantId}/governance/rights/{realmId}")
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin);

        group.MapGet("", async (
            string tenantId,
            string realmId,
            IFunctionalRightRepository repository,
            CancellationToken ct) =>
        {
            var mappings = await repository.GetMappingsAsync(new TenantId(tenantId), realmId, ct).ConfigureAwait(false);
            return Results.Ok(mappings);
        }).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapPost("", async (
            string tenantId,
            string realmId,
            ProposeMappingRequest request,
            HttpContext httpContext,
            IFunctionalRightMapper mapper,
            CancellationToken ct) =>
        {
            var createdBy = httpContext.User.GetUserSid()?.Value ?? "SYSTEM";
            var mapping = await mapper.ProposeMappingAsync(
                new TenantId(tenantId),
                realmId,
                request.GroupOrRole,
                new FunctionalRight(request.FunctionalRight),
                request.RecertClass ?? RecertClass.Standard,
                createdBy,
                ct).ConfigureAwait(false);

            return Results.Created($"/api/v1/tenants/{tenantId}/governance/rights/{realmId}", mapping);
        }).WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        group.MapPost("/approve", async (
            string tenantId,
            string realmId,
            ApproveMappingRequest request,
            HttpContext httpContext,
            IFunctionalRightMapper mapper,
            CancellationToken ct) =>
        {
            var approvedBy = httpContext.User.GetUserSid()?.Value ?? "APPROVER";
            await mapper.ApproveMappingAsync(
                new TenantId(tenantId),
                realmId,
                request.GroupOrRole,
                new FunctionalRight(request.FunctionalRight),
                approvedBy,
                ct).ConfigureAwait(false);

            return Results.Ok(new { message = "Mapping approved successfully." });
        }).WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        group.MapDelete("", async (
            string tenantId,
            string realmId,
            string groupOrRole,
            string functionalRight,
            HttpContext httpContext,
            IFunctionalRightMapper mapper,
            CancellationToken ct) =>
        {
            var deletedBy = httpContext.User.GetUserSid()?.Value ?? "SYSTEM";
            await mapper.DeleteMappingAsync(
                new TenantId(tenantId),
                realmId,
                groupOrRole,
                new FunctionalRight(functionalRight),
                deletedBy,
                ct).ConfigureAwait(false);

            return Results.NoContent();
        }).WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }
}
