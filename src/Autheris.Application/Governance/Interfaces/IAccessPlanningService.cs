namespace Autheris.Application.Governance.Interfaces;

using System.Security.Claims;
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
        string? tenantId = null,
        ClaimsPrincipal? caller = null,
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
        TenantId tenantId,
        ClaimsPrincipal? caller = null,
        CancellationToken ct = default);

    /// <summary>
    /// Issue #107: plans a change of the row filter key-set cache settings of one rule or one object. Validated against the current caps; no state
    /// mutation. Confirmation uses <see cref="ConfirmPlanAsync"/> (TOTP), the change itself <see cref="ApplyRowFilterEvaluationAsync"/>.
    /// </summary>
    Task<AdminPlanRowFilterEvaluationResult> PlanRowFilterEvaluationAsync(
        AdminPlanRowFilterEvaluationRequest request,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Issue #107: applies a confirmed row filter evaluation plan. Rejects plans of another kind, validates again against the current caps and
    /// rejects the plan when the stored settings changed since planning.
    /// </summary>
    Task<AdminApplyRowFilterEvaluationResult> ApplyRowFilterEvaluationAsync(
        AdminApplyAccessRequest request,
        string adminSid,
        TenantId tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Plans a mutating MCP administrator operation (two-phase confirmation, ADR-03) without executing it. The plan binds the operation
    /// (tool name) and the SHA-256 digest of its canonical arguments; <paramref name="target"/> is the human-readable object the
    /// confirming administrator sees in the plan. Confirmation uses <see cref="ConfirmPlanAsync"/> (TOTP) like an access plan.
    /// </summary>
    Task<AdminPlanAccessResult> PlanAdminOperationAsync(
        string operation,
        string target,
        string argumentsDigest,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies and consumes (once, cluster-wide) the confirmation token of a <see cref="PlanAdminOperationAsync"/> plan with the checks of
    /// every apply (plan kind, expiry, administrator, tenant, token). Throws <see cref="System.Security.SecurityException"/> when any check
    /// fails or when the plan was made for another operation or other arguments; the caller runs the operation only after this returns.
    /// </summary>
    Task ConsumeAdminOperationAsync(
        AdminApplyAccessRequest request,
        string operation,
        string argumentsDigest,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Registers a new datasource in inactive status (SEC M-30) and isolates credentials in vault.
    /// </summary>
    Task<AdminRegisterDatasourceResult> RegisterDatasourceAsync(
        AdminRegisterDatasourceRequest request,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Updates dataset lifecycle status (active, quarantined, deprecated, inactive) and records WORM audit.
    /// </summary>
    Task<AdminSetDatasetStateResult> SetDatasetStateAsync(
        AdminSetDatasetStateRequest request,
        string adminSid,
        string? tenantId = null,
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
    Task<AdminPlanAccessResult?> GetPlanAsync(string planId, CancellationToken ct = default);
}
