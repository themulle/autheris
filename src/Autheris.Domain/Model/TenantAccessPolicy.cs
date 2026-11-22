using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Security;

namespace Autheris.Domain.Model;

/// <summary>Effective internal-audience rules resolved for a tenant access policy.</summary>
public sealed record InternalAudiencePolicy(
    IReadOnlyList<string> TrustedIssuerTenantIds,
    IReadOnlyList<string> EmployeeGroupSids,
    bool RequireMemberIndicator = true,
    IReadOnlyList<string>? TrustedServicePrincipalClientIds = null)
{
    public IReadOnlyList<string> TrustedServicePrincipalClientIds { get; init; } =
        TrustedServicePrincipalClientIds ?? [];
}

/// <summary>Nullable tenant override payload stored per tenant and clamped by deployment policy ceilings.</summary>
public sealed record InternalAudiencePolicyUpdate(
    IReadOnlyList<string>? TrustedIssuerTenantIds = null,
    IReadOnlyList<string>? EmployeeGroupSids = null,
    bool? RequireMemberIndicator = null,
    IReadOnlyList<string>? TrustedServicePrincipalClientIds = null);

/// <summary>Nullable tenant override payload accepted on policy updates and persisted in settings storage.</summary>
public sealed record TenantAccessPolicyUpdate(
    int? MaxDataOwnerDays = null,
    int? MaxGovernanceDays = null,
    int? MaxItAdminDays = null,
    int? MaxSelfGrantDays = null,
    ProtectionClass? SelfGrantCeiling = null,
    IReadOnlyList<int>? ReminderDays = null,
    CatalogVisibilityMode? DefaultContainerVisibility = null,
    InternalAudiencePolicyUpdate? Internal = null,
    bool? ServeImportSamples = null,
    bool? QuarantineSuspectedPiiSamples = null,
    int? MaxBundleItems = null,
    int? MaxPendingRequestsPerRequester = null,
    int? MaxTablesPerAssignment = null,
    int? MaxPageSize = null);

/// <summary>Stored tenant-level access policy row with optimistic concurrency version.</summary>
public sealed record StoredTenantAccessPolicy(
    TenantId Tenant,
    TenantAccessPolicyUpdate Settings,
    int Version,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>Effective resolved access policy for a tenant after clamping by deployment ceilings.</summary>
public sealed record TenantAccessPolicy(
    TenantId Tenant,
    int MaxDataOwnerDays,
    int MaxGovernanceDays,
    int MaxItAdminDays,
    int MaxSelfGrantDays,
    ProtectionClass SelfGrantCeiling,
    IReadOnlyList<int> ReminderDays,
    CatalogVisibilityMode DefaultContainerVisibility,
    InternalAudiencePolicy Internal,
    bool ServeImportSamples,
    bool QuarantineSuspectedPiiSamples,
    int MaxBundleItems,
    int MaxPendingRequestsPerRequester,
    int MaxTablesPerAssignment,
    int MaxPageSize,
    int Version = 0,
    string? UpdatedBy = null,
    DateTimeOffset? UpdatedAt = null)
{
    public static TenantAccessPolicy FromDeploymentDefaults(TenantId tenant, AccessPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var internalCfg = options.Internal ?? new InternalAccessPolicySettings();
        return new TenantAccessPolicy(
            Tenant: tenant,
            MaxDataOwnerDays: Math.Clamp(options.MaxDataOwnerDays, 1, 36500),
            MaxGovernanceDays: Math.Clamp(options.MaxGovernanceDays, 1, 36500),
            MaxItAdminDays: Math.Clamp(options.MaxItAdminDays, 1, 3650),
            MaxSelfGrantDays: Math.Clamp(options.MaxSelfGrantDays, 1, 3650),
            SelfGrantCeiling: options.SelfGrantCeiling is ProtectionClass.Public or ProtectionClass.Internal or ProtectionClass.Confidential
                ? options.SelfGrantCeiling
                : ProtectionClass.Confidential,
            ReminderDays: (options.ReminderDays ?? [])
                .Where(d => d is >= 1 and <= 365)
                .Distinct()
                .OrderByDescending(d => d)
                .Take(10)
                .ToArray(),
            DefaultContainerVisibility: options.DefaultContainerVisibility.NormalizeOrOnRequest(),
            Internal: new InternalAudiencePolicy(
                (internalCfg.TrustedIssuerTenantIds ?? []).ToArray(),
                (internalCfg.EmployeeGroupSids ?? []).ToArray(),
                internalCfg.RequireMemberIndicator,
                (internalCfg.TrustedServicePrincipalClientIds ?? []).ToArray()),
            ServeImportSamples: options.ServeImportSamples,
            QuarantineSuspectedPiiSamples: options.QuarantineSuspectedPiiSamples,
            MaxBundleItems: Math.Clamp(options.MaxBundleItems, 1, 1000),
            MaxPendingRequestsPerRequester: Math.Clamp(options.MaxPendingRequestsPerRequester, 1, 10000),
            MaxTablesPerAssignment: Math.Clamp(options.MaxTablesPerAssignment, 1, 10000),
            MaxPageSize: Math.Clamp(options.MaxPageSize, 1, 5000));
    }
}
