namespace Autheris.Api.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Security;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Common;
using Autheris.Domain.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

public static partial class ConsentWorkflowAdminEndpoints
{
    internal static async Task<IResult> ExportGrantRecordAsync(
        Guid grantId,
        HttpContext context,
        IGrantRecordExportService exportService,
        [FromQuery(Name = "tenantId")] string? tenantId = null,
        CancellationToken ct = default)
    {
        if (!TryAuthorizeGrantRecordCaller(context, tenantId, out var targetTenant, out var errorResult))
        {
            return errorResult!;
        }

        var bundle = await exportService.ExportForGrantAsync(targetTenant, grantId, ct).ConfigureAwait(false);
        return bundle is null
            ? ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, "Grant record was not found.", context)
            : Results.Ok(bundle);
    }

    internal static async Task<IResult> VerifyGrantRecordsAsync(
        GrantRecordVerifyRequest? body,
        HttpContext context,
        IGrantRecordExportService exportService,
        CancellationToken ct = default)
    {
        if (!TryAuthorizeGrantRecordCaller(context, body?.TenantId, out var targetTenant, out var errorResult))
        {
            return errorResult!;
        }

        if (body is null)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Verification request body is required.", context);
        }

        var result = await exportService.VerifyAsync(targetTenant, body, ct).ConfigureAwait(false);
        return result is null
            ? ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, "Grant record was not found.", context)
            : Results.Ok(result);
    }

    private static bool TryAuthorizeGrantRecordCaller(
        HttpContext context,
        string? requestedTenantId,
        out TenantId targetTenant,
        out IResult? errorResult)
    {
        targetTenant = default;

        if (context.User?.Identity?.IsAuthenticated != true)
        {
            errorResult = ApiProblems.Unauthorized(httpContext: context);
            return false;
        }

        if (!GatewayPolicies.HasAnyRole(context.User, GatewayPolicies.AuditorOrAboveRoles))
        {
            errorResult = ApiProblems.Forbidden(
                ApiErrorCodes.AccessDenied,
                "Grant record export and verification require Auditor, Compliance, GovernanceAdmin, TenantAdmin, or ClusterAdmin access.",
                context);
            return false;
        }

        var callerTenant = EndpointSecurity.GetRequestTenant(context);
        if (string.IsNullOrWhiteSpace(requestedTenantId))
        {
            targetTenant = callerTenant;
            errorResult = null;
            return true;
        }

        if (!TenantId.TryParse(requestedTenantId.Trim(), out var parsedTenant))
        {
            errorResult = ApiProblems.BadRequest(ApiErrorCodes.InvalidTenantId, "Invalid tenantId.", context);
            return false;
        }

        if (parsedTenant != callerTenant && !EndpointSecurity.IsCanonicalClusterAdmin(context.User))
        {
            errorResult = ApiProblems.Forbidden(
                ApiErrorCodes.CrossTenantAccessForbidden,
                "Cross-tenant grant record access requires ClusterAdmin.",
                context);
            return false;
        }

        targetTenant = parsedTenant;
        errorResult = null;
        return true;
    }
}
