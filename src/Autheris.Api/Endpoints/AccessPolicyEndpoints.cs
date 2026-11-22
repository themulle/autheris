using System.Globalization;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Policy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Autheris.Api.Endpoints;

/// <summary>Tenant access-policy settings endpoints (<c>/api/v1/governance/policy</c>).</summary>
public static class AccessPolicyEndpoints
{
    public const string PolicyRoute = "/api/v1/governance/policy";

    public static IEndpointRouteBuilder MapAccessPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(PolicyRoute, GetPolicyAsync)
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Governance");

        app.MapPut(PolicyRoute, UpdatePolicyAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Full, AuditEventTypes.PolicyChanged)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Governance");

        return app;
    }

    internal static async Task<IResult> GetPolicyAsync(
        HttpContext http,
        [FromServices] IGovernanceCapabilities capabilities,
        [FromServices] IAccessPolicySettingsProvider provider,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        if (!capabilities.CanEditPolicy(sec.Principal, sec.TenantId))
        {
            return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Only Data Governance administrators can read tenant access policy settings.", http);
        }

        var policy = await provider.GetEffectiveAsync(sec.TenantId, ct).ConfigureAwait(false);
        http.Response.Headers.ETag = FormatETag(policy.Version);
        return Results.Ok(ToResponse(policy));
    }

    internal static async Task<IResult> UpdatePolicyAsync(
        HttpContext http,
        [FromBody] TenantAccessPolicyUpdateRequest? request,
        [FromServices] IGovernanceCapabilities capabilities,
        [FromServices] IAccessPolicySettingsProvider provider,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        if (!capabilities.CanEditPolicy(sec.Principal, sec.TenantId))
        {
            return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Only Data Governance administrators can update tenant access policy settings.", http);
        }

        if (!TryParseIfMatchVersion(http, allowZero: true, out var expectedVersion, out var ifMatchError))
        {
            return ifMatchError!;
        }

        if (request is null)
        {
            return ApiProblems.Unprocessable("INVALID_ACCESS_POLICY", "Tenant access policy request is required.", http);
        }

        if (!TryMapUpdate(request, out var mapped, out var parseError))
        {
            return ApiProblems.Unprocessable("INVALID_ACCESS_POLICY", parseError ?? "Invalid tenant access policy request.", http);
        }

        var result = await provider.UpdateAsync(sec.TenantId, mapped, expectedVersion, sec.UserSid, ct).ConfigureAwait(false);
        return result.Status switch
        {
            AccessPolicyUpdateStatus.Updated when result.Policy is not null => OkWithETag(http, result.Policy),
            AccessPolicyUpdateStatus.Conflict => ApiProblems.Problem(
                StatusCodes.Status412PreconditionFailed,
                ApiErrorCodes.VersionMismatch,
                result.ErrorMessage ?? "Tenant access policy version does not match If-Match.",
                http,
                title: "Precondition Failed"),
            _ => ApiProblems.Unprocessable(
                result.ErrorCode ?? "INVALID_ACCESS_POLICY",
                result.ErrorMessage ?? "Tenant access policy validation failed.",
                http)
        };
    }

    internal static bool TryParseIfMatchVersion(HttpContext context, bool allowZero, out int expectedVersion, out IResult? error)
    {
        if (!context.Request.Headers.TryGetValue("If-Match", out var values))
        {
            expectedVersion = 0;
            error = ApiProblems.Problem(
                StatusCodes.Status428PreconditionRequired,
                ApiErrorCodes.PreconditionRequired,
                "An If-Match ETag header is required for policy updates.",
                context,
                title: "Precondition Required");
            return false;
        }

        var raw = values.ToString().Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            expectedVersion = 0;
            error = ApiProblems.Problem(
                StatusCodes.Status428PreconditionRequired,
                ApiErrorCodes.PreconditionRequired,
                "If-Match header cannot be empty.",
                context,
                title: "Precondition Required");
            return false;
        }

        var normalized = raw;
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var min = allowZero ? 0 : 1;
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= min)
        {
            expectedVersion = parsed;
            error = null;
            return true;
        }

        expectedVersion = 0;
        error = ApiProblems.Problem(
            StatusCodes.Status412PreconditionFailed,
            ApiErrorCodes.VersionMismatch,
            "Invalid If-Match ETag format.",
            context,
            title: "Precondition Failed");
        return false;
    }

    private static IResult OkWithETag(HttpContext http, TenantAccessPolicy policy)
    {
        http.Response.Headers.ETag = FormatETag(policy.Version);
        return Results.Ok(ToResponse(policy));
    }

    private static string FormatETag(int version) => string.Create(CultureInfo.InvariantCulture, $"\"{Math.Max(0, version)}\"");

    private static bool TryMapUpdate(TenantAccessPolicyUpdateRequest req, out TenantAccessPolicyUpdate update, out string? error)
    {
        ProtectionClass? selfGrantCeiling = null;
        if (!string.IsNullOrWhiteSpace(req.SelfGrantCeiling))
        {
            if (!ProtectionClassExtensions.TryParse(req.SelfGrantCeiling, out var parsedClass))
            {
                update = default!;
                error = $"Unknown protection class '{req.SelfGrantCeiling}' for selfGrantCeiling.";
                return false;
            }

            selfGrantCeiling = parsedClass;
        }

        CatalogVisibilityMode? visibility = null;
        if (!string.IsNullOrWhiteSpace(req.DefaultContainerVisibility))
        {
            if (!TryParseVisibility(req.DefaultContainerVisibility, out var parsedVisibility))
            {
                update = default!;
                error = $"Unknown catalog visibility mode '{req.DefaultContainerVisibility}'.";
                return false;
            }

            visibility = parsedVisibility;
        }

        update = new TenantAccessPolicyUpdate(
            MaxDataOwnerDays: req.MaxDataOwnerDays,
            MaxGovernanceDays: req.MaxGovernanceDays,
            MaxItAdminDays: req.MaxItAdminDays,
            MaxSelfGrantDays: req.MaxSelfGrantDays,
            SelfGrantCeiling: selfGrantCeiling,
            ReminderDays: req.ReminderDays,
            DefaultContainerVisibility: visibility,
            Internal: req.Internal is null
                ? null
                : new InternalAudiencePolicyUpdate(
                    req.Internal.TrustedIssuerTenantIds,
                    req.Internal.EmployeeGroupSids,
                    req.Internal.RequireMemberIndicator,
                    req.Internal.TrustedServicePrincipalClientIds),
            ServeImportSamples: req.ServeImportSamples,
            QuarantineSuspectedPiiSamples: req.QuarantineSuspectedPiiSamples,
            MaxBundleItems: req.MaxBundleItems,
            MaxPendingRequestsPerRequester: req.MaxPendingRequestsPerRequester,
            MaxTablesPerAssignment: req.MaxTablesPerAssignment,
            MaxPageSize: req.MaxPageSize,
            NotificationWebhookUrl: req.NotificationWebhookUrl);
        error = null;
        return true;
    }

    private static bool TryParseVisibility(string raw, out CatalogVisibilityMode mode)
    {
        var trimmed = raw.Trim();
        if (string.Equals(trimmed, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            mode = CatalogVisibilityMode.Open;
            return true;
        }

        if (string.Equals(trimmed, "ON_REQUEST", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "OnRequest", StringComparison.OrdinalIgnoreCase))
        {
            mode = CatalogVisibilityMode.OnRequest;
            return true;
        }

        mode = default;
        return false;
    }

    private static object ToResponse(TenantAccessPolicy p) => new
    {
        tenantId = p.Tenant.Value,
        maxDataOwnerDays = p.MaxDataOwnerDays,
        maxGovernanceDays = p.MaxGovernanceDays,
        maxItAdminDays = p.MaxItAdminDays,
        maxSelfGrantDays = p.MaxSelfGrantDays,
        selfGrantCeiling = p.SelfGrantCeiling.ToCanonicalName(),
        reminderDays = p.ReminderDays,
        defaultContainerVisibility = p.DefaultContainerVisibility.ToCanonicalName(),
        @internal = new
        {
            trustedIssuerTenantIds = p.Internal.TrustedIssuerTenantIds,
            employeeGroupSids = p.Internal.EmployeeGroupSids,
            requireMemberIndicator = p.Internal.RequireMemberIndicator,
            trustedServicePrincipalClientIds = p.Internal.TrustedServicePrincipalClientIds
        },
        serveImportSamples = p.ServeImportSamples,
        quarantineSuspectedPiiSamples = p.QuarantineSuspectedPiiSamples,
        maxBundleItems = p.MaxBundleItems,
        maxPendingRequestsPerRequester = p.MaxPendingRequestsPerRequester,
        maxTablesPerAssignment = p.MaxTablesPerAssignment,
        maxPageSize = p.MaxPageSize,
        notificationWebhookConfigured = p.NotificationWebhookConfigured,
        notificationWebhookHost = p.NotificationWebhookHost,
        version = p.Version,
        updatedBy = p.UpdatedBy,
        updatedAt = p.UpdatedAt
    };

    public sealed record InternalAudienceUpdateRequest(
        IReadOnlyList<string>? TrustedIssuerTenantIds = null,
        IReadOnlyList<string>? EmployeeGroupSids = null,
        bool? RequireMemberIndicator = null,
        IReadOnlyList<string>? TrustedServicePrincipalClientIds = null);

    public sealed record TenantAccessPolicyUpdateRequest(
        int? MaxDataOwnerDays = null,
        int? MaxGovernanceDays = null,
        int? MaxItAdminDays = null,
        int? MaxSelfGrantDays = null,
        string? SelfGrantCeiling = null,
        IReadOnlyList<int>? ReminderDays = null,
        string? DefaultContainerVisibility = null,
        InternalAudienceUpdateRequest? Internal = null,
        bool? ServeImportSamples = null,
        bool? QuarantineSuspectedPiiSamples = null,
        int? MaxBundleItems = null,
        int? MaxPendingRequestsPerRequester = null,
        int? MaxTablesPerAssignment = null,
        int? MaxPageSize = null,
        string? NotificationWebhookUrl = null);
}
