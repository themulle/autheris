namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Governance.Rights;
using Autheris.Application.Governance.SingleApproval;
using Autheris.Api.Endpoints.Support;
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
    public sealed record SaveMappingRequest(string GroupOrRole, string FunctionalRight, string? RecertClass);

    public static IEndpointRouteBuilder MapFunctionalRightEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenants/{tenantId}/governance/rights/{realmId}")
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .RequireTenantRouteMatch();

        group.MapGet("", async (
            string tenantId,
            string realmId,
            IFunctionalRightRepository repository,
            CancellationToken ct) =>
        {
            var mappings = await repository.GetMappingsAsync(new TenantId(tenantId), realmId, ct).ConfigureAwait(false);
            return Results.Ok(mappings);
        }).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // Single approval: the save of a governance admin is the approval; the mapping is effective immediately.
        group.MapPost("", async (
            string tenantId,
            string realmId,
            SaveMappingRequest request,
            HttpContext httpContext,
            IFunctionalRightMapper mapper,
            CancellationToken ct) =>
        {
            if (Writer(httpContext) is not { } writer)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return await RefusedAsProblemAsync(httpContext, async () =>
            {
                var mapping = await mapper.SaveMappingAsync(
                    new TenantId(tenantId),
                    realmId,
                    request.GroupOrRole,
                    new FunctionalRight(request.FunctionalRight),
                    string.IsNullOrWhiteSpace(request.RecertClass) ? "Standard" : request.RecertClass,
                    writer,
                    ct).ConfigureAwait(false);

                return Results.Created($"/api/v1/tenants/{tenantId}/governance/rights/{realmId}", mapping);
            }).ConfigureAwait(false);
        }).WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        group.MapDelete("", async (
            string tenantId,
            string realmId,
            string groupOrRole,
            string functionalRight,
            HttpContext httpContext,
            IFunctionalRightMapper mapper,
            CancellationToken ct) =>
        {
            if (Writer(httpContext) is not { } writer)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return await RefusedAsProblemAsync(httpContext, async () =>
            {
                await mapper.DeleteMappingAsync(
                    new TenantId(tenantId),
                    realmId,
                    groupOrRole,
                    new FunctionalRight(functionalRight),
                    writer,
                    ct).ConfigureAwait(false);

                return Results.NoContent();
            }).ConfigureAwait(false);
        }).WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }

    // The writer of a change: subject key, every identifier form and the caller's groups and roles (a mapping key is a group or a role),
    // which feed the self-widening check. Without a subject key there is no writer (fail closed).
    private static SingleApprovalWriter? Writer(HttpContext httpContext)
    {
        var subject = httpContext.User.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var identifiers = httpContext.User.GetUserIdentifiers();
        identifiers.Add(subject);
        var groupsAndRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in httpContext.User.GetGroupSids())
        {
            groupsAndRoles.Add(group.Value);
        }

        groupsAndRoles.UnionWith(httpContext.User.GetUserRoles());
        return new SingleApprovalWriter(subject, identifiers, groupsAndRoles);
    }

    private static async Task<IResult> RefusedAsProblemAsync(HttpContext httpContext, Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (SingleApprovalRefusedException ex)
        {
            int status = ex.Code == "AuditFailed" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status403Forbidden;
            return SafeErrorResults.Problem(status, "FUNCTIONAL_RIGHT_" + ex.Code.ToUpperInvariant(), "The functional-right change was refused.", httpContext);
        }
        catch (SecurityException)
        {
            return SafeErrorResults.Problem(StatusCodes.Status403Forbidden, "FUNCTIONAL_RIGHT_NOT_CONFERRABLE", "The functional-right change was refused.", httpContext);
        }
    }
}
