namespace Autheris.Modules.Lineage.Endpoints;

using System;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Autheris.Modules.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

internal static class LineageEndpoints
{
    internal const string GovernanceAdminPolicy = "GovernanceAdmin";
    private static readonly IGatewayRoleEvaluator RoleEvaluator = new GatewayRoleEvaluator();

    public static void Map(IModuleEndpointRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);

        var group = registrar.MapGroup(LineageModule.RouteGroupPrefix);

        group.MapPost("/openlineage/sync", (
                [FromServices] IOpenLineageClient openLineageClient,
                [FromServices] IModuleRequestContext requestContext,
                HttpContext context,
                CancellationToken ct) => SyncOpenLineageAsync(openLineageClient, requestContext, context, ct))
            .RequireAuthorization(GovernanceAdminPolicy)
            .WithModuleAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);
    }

    internal static async Task<IResult> SyncOpenLineageAsync(
        IOpenLineageClient openLineageClient,
        IModuleRequestContext requestContext,
        HttpContext context,
        CancellationToken ct)
    {
        if (!RoleEvaluator.HasRole(context.User, GatewayRole.GovernanceAdmin))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        TenantId tenantId;
        try
        {
            tenantId = requestContext.GetRequestTenant(context);
        }
        catch (SecurityException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }
        catch (ArgumentException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var success = await openLineageClient.PushLineageGraphAsync(tenantId, ct);
        if (success)
        {
            return Results.Ok(new { message = "OpenLineage sync completed successfully." });
        }

        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }
}
