namespace Autheris.Application.Governance.Interfaces;

/// <summary>Stored settings of one level and the resulting effective (clamped) settings.</summary>
public sealed record RowFilterEvaluationView(RowFilterEvaluationSettings Stored, EffectiveRowFilterEvaluation Effective);

/// <summary>The validated target of a planned change: its target table and the currently stored settings of the level (the plan's "before").</summary>
public sealed record RowFilterEvaluationPreview(TableIdentifier Table, RowFilterEvaluationSettings Before);

/// <summary>The requested settings are not acceptable (caps, range, rule type); the reason names no stored data. Mapped to 400 by the REST surface.</summary>
public sealed class RowFilterEvaluationValidationException(string reason) : Exception(reason)
{
    /// <summary>The curated, fixed-text reason produced by <c>RowFilterEvaluationResolver.Validate</c> (names caps and rule types, never stored data).</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Issue #107 (PLAN-GOV-107 C-3, SEC-RF107-14): shared by the REST admin endpoints and the MCP plan/apply tools. Validates, writes through
/// the repository (settings change and epoch bump in one transaction) and records an HMAC-chained audit entry with before/after.
/// Methods return null when the rule or table does not exist (or the rule belongs to another tenant); invalid settings throw
/// <see cref="RowFilterEvaluationValidationException"/>.
/// </summary>
public interface IRowFilterEvaluationAdminService
{
    Task<RowFilterEvaluationView?> GetRuleAsync(Guid rowFilterId, TenantId tenant, CancellationToken ct);
    Task<RowFilterEvaluationChange?> SetRuleAsync(Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings settings, Sid actor, CancellationToken ct);
    Task<RowFilterEvaluationView?> GetObjectAsync(TableIdentifier table, CancellationToken ct);
    /// <param name="settings">Null clears the object level (all inherit).</param>
    /// <param name="tenant">The caller's tenant (audit only; the object level is not tenant scoped).</param>
    Task<RowFilterEvaluationChange?> SetObjectAsync(TableIdentifier table, RowFilterEvaluationSettings? settings, Sid actor, TenantId tenant, CancellationToken ct);

    /// <summary>Validates a rule-level change against the current caps and rule type without writing; null when not found or foreign tenant.</summary>
    Task<RowFilterEvaluationPreview?> ValidateRuleAsync(Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings settings, CancellationToken ct);

    /// <summary>Validates an object-level change against the current caps without writing; null when the table is unknown.</summary>
    Task<RowFilterEvaluationPreview?> ValidateObjectAsync(TableIdentifier table, RowFilterEvaluationSettings? settings, CancellationToken ct);
}
