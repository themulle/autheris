using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Domain.Options;

/// <summary>
/// Deployment defaults and hard upper ceilings for tenant-configurable access policies
/// (<c>Gateway:Governance:AccessPolicy</c>). Tenant overrides may only tighten these bounds.
/// </summary>
public sealed class AccessPolicyOptions
{
    public const string SectionName = GatewaySections.AccessPolicy;

    /// <summary>
    /// Maximum grant lifetime in days when approved by a container owner-group member (default 3650).
    /// </summary>
    [Range(1, 36500)]
    public int MaxDataOwnerDays { get; init; } = 3650;

    /// <summary>
    /// Maximum grant lifetime in days when approved by Data Governance (default 3650).
    /// </summary>
    [Range(1, 36500)]
    public int MaxGovernanceDays { get; init; } = 3650;

    /// <summary>
    /// Maximum grant lifetime in days when approved by an IT administrator (default 365).
    /// </summary>
    [Range(1, 3650)]
    public int MaxItAdminDays { get; init; } = 365;

    /// <summary>
    /// Maximum lifetime in days for an IT-administrator self-grant (default 365).
    /// </summary>
    [Range(1, 3650)]
    public int MaxSelfGrantDays { get; init; } = 365;

    /// <summary>
    /// Highest object protection class eligible for an IT-administrator self-grant (<c>Secret</c> is rejected).
    /// </summary>
    public ProtectionClass SelfGrantCeiling { get; init; } = ProtectionClass.Confidential;

    /// <summary>
    /// Days before grant expiry at which reminder notifications are emitted (1 to 365, at most 10 entries).
    /// </summary>
    public List<int> ReminderDays { get; init; } = [30, 7];

    /// <summary>
    /// Default catalog visibility mode assigned to new governance containers (default <c>OnRequest</c>).
    /// </summary>
    public CatalogVisibilityMode DefaultContainerVisibility { get; init; } = CatalogVisibilityMode.OnRequest;

    /// <summary>
    /// Settings controlling which authenticated principals qualify as the internal audience.
    /// </summary>
    public InternalAccessPolicySettings Internal { get; init; } = new();

    /// <summary>
    /// Enables serving dbt import synthetic samples to catalog-visible callers (default true).
    /// </summary>
    public bool ServeImportSamples { get; init; } = true;

    /// <summary>
    /// Suppresses imported synthetic samples flagged for suspected PII patterns (default false).
    /// </summary>
    public bool QuarantineSuspectedPiiSamples { get; init; } = false;

    /// <summary>
    /// Maximum number of target items permitted in a single bundled permission request (default 50).
    /// </summary>
    [Range(1, 1000)]
    public int MaxBundleItems { get; init; } = 50;

    /// <summary>
    /// Maximum number of concurrent pending permission requests permitted per requester (default 100).
    /// </summary>
    [Range(1, 10000)]
    public int MaxPendingRequestsPerRequester { get; init; } = 100;

    /// <summary>
    /// Maximum number of table identifiers accepted per domain table-assignment call, and the bound on catalog rows evaluated by the container visibility rule per listing and on data grants read into a visibility snapshot (default 1000).
    /// </summary>
    [Range(1, 10000)]
    public int MaxTablesPerAssignment { get; init; } = 1000;

    /// <summary>
    /// Maximum page size permitted by governance listing endpoints, and the bound on containers read into a catalog visibility snapshot (default 500).
    /// </summary>
    [Range(1, 5000)]
    public int MaxPageSize { get; init; } = 500;
}

/// <summary>
/// Deployment defaults and allowlists for resolving the internal audience within tenant access policies.
/// </summary>
public sealed class InternalAccessPolicySettings
{
    /// <summary>
    /// Trusted Entra issuer tenant GUIDs permitted for internal audience recognition (empty by default).
    /// </summary>
    public List<string> TrustedIssuerTenantIds { get; init; } = [];

    /// <summary>
    /// Group SIDs whose members qualify as internal employees (empty by default).
    /// </summary>
    public List<string> EmployeeGroupSids { get; init; } = [];

    /// <summary>
    /// Requires non-guest member account indication unless membership in <see cref="EmployeeGroupSids"/> is proven (default true).
    /// </summary>
    public bool RequireMemberIndicator { get; init; } = true;

    /// <summary>
    /// Registered service principal client identifiers permitted for internal audience classification (empty by default).
    /// </summary>
    public List<string> TrustedServicePrincipalClientIds { get; init; } = [];
}
