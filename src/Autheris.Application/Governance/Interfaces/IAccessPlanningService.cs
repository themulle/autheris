namespace Autheris.Application.Governance.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Service managing two-phase access planning, TOTP 2FA confirmation,
/// ReBAC mutations, and administrative control plane operations.
/// </summary>
public interface IAccessPlanningService
{
    /// <summary>
    /// Generates an access plan with column-level diffs and PII warnings without mutating state.
    /// </summary>
    Task<AdminPlanAccessResult> PlanAccessAsync(
        AdminPlanAccessRequest request,
        string adminSid,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies TOTP 2FA for an access plan and issues an HMAC-signed confirmation token with TTL.
    /// </summary>
    Task<AdminConfirmPlanResult> ConfirmPlanAsync(
        string planId,
        string totpCode,
        string adminSid,
        CancellationToken ct = default);

    /// <summary>
    /// Applies an approved access plan using a valid confirmation token, updating ReBAC and recording WORM audit.
    /// </summary>
    Task<AdminApplyAccessResult> ApplyAccessAsync(
        AdminApplyAccessRequest request,
        string adminSid,
        CancellationToken ct = default);

    /// <summary>
    /// Registers a new datasource in inactive status (SEC M-30) and isolates credentials in vault.
    /// </summary>
    Task<AdminRegisterDatasourceResult> RegisterDatasourceAsync(
        AdminRegisterDatasourceRequest request,
        string adminSid,
        CancellationToken ct = default);

    /// <summary>
    /// Updates dataset lifecycle status (active, quarantined, deprecated, inactive) and records WORM audit.
    /// </summary>
    Task<AdminSetDatasetStateResult> SetDatasetStateAsync(
        AdminSetDatasetStateRequest request,
        string adminSid,
        CancellationToken ct = default);

    /// <summary>
    /// Performs fuzzy/prefix resolution of principals by display name, account, or SID.
    /// </summary>
    Task<AdminResolvePrincipalResult> ResolvePrincipalAsync(
        AdminResolvePrincipalRequest request,
        string adminSid,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves a pending access plan by ID, or null if not found or expired.
    /// </summary>
    AdminPlanAccessResult? GetPlan(string planId);
}
