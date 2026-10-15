namespace Autheris.Application.Governance.Interfaces;

/// <summary>Stored settings of one level and the resulting effective (clamped) settings.</summary>
public sealed record RowFilterEvaluationView(RowFilterEvaluationSettings Stored, EffectiveRowFilterEvaluation Effective);

/// <summary>
/// Issue #107 (PLAN-GOV-107 C-3, SEC-RF107-14): shared by the REST admin endpoints and the MCP plan/apply tools. Validates, writes through
/// the repository (settings change and epoch bump in one transaction) and records an HMAC-chained audit entry with before/after.
/// Methods return null when the rule or table does not exist (or the rule belongs to another tenant).
/// </summary>
public interface IRowFilterEvaluationAdminService
{
    Task<RowFilterEvaluationView?> GetRuleAsync(Guid rowFilterId, TenantId tenant, CancellationToken ct);
    Task<RowFilterEvaluationChange?> SetRuleAsync(Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings settings, Sid actor, CancellationToken ct);
    Task<RowFilterEvaluationView?> GetObjectAsync(TableIdentifier table, CancellationToken ct);
    /// <param name="settings">Null clears the object level (all inherit).</param>
    Task<RowFilterEvaluationChange?> SetObjectAsync(TableIdentifier table, RowFilterEvaluationSettings? settings, Sid actor, CancellationToken ct);
}
