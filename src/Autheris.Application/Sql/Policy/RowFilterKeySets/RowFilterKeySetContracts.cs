namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

/// <summary>
/// Issue #107 (PLAN-GOV-107 A-2): per-statement stage that substitutes eligible correlated <c>EXISTS</c> sites by cached key sets.
/// It rewrites the compiler input only, never a decision, recipe or cache (SEC-RF107-04, -10). WP-C owns the implementation.
/// </summary>
public interface IRowFilterKeySetStage
{
    ValueTask<IReadOnlyDictionary<TableIdentity, PolicyPredicate?>> ApplyAsync(
        RowFilterKeySetContext context,
        IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates,
        RowFilterKeySetReport report,
        CancellationToken ct);
}

/// <param name="GovernanceIdentifiers">Target and dependent governance identifiers per table (the epoch keys).</param>
/// <param name="Disabled">True on the single bind-limit retry (SEC-RF107-15): every site stays <c>EXISTS</c>.</param>
public sealed record RowFilterKeySetContext(
    string DataSourceName,
    TenantId Tenant,
    TargetSqlDialect Dialect,
    ITableCatalog Catalog,
    TenantBinding TenantBinding,
    IReadOnlyDictionary<TableIdentity, TableMetadata?> Metadata,
    IReadOnlyDictionary<TableIdentity, IReadOnlyList<TableIdentifier>> GovernanceIdentifiers,
    bool Disabled);

public enum KeySetOutcomeKind { Exists, Hit, Miss, Fallback }

/// <summary><c>Deny_Negated</c> is the reason of a site in a negated context or a shared node instance (SEC-RF107-06).</summary>
public enum KeySetFallbackReason
{
    Deny_Negated,
    Temporal,
    NotDerivable,
    Overflow,
    ParameterBudget,
    BindLimitRetry,
    EpochUnavailable,
    DialectUnsupported,
    SourceNotEligible,
    TypeNotBindable,
    ValueNotBindable,
    InvalidStoredSetting,
    LoadThrottled
}

/// <summary>Diagnostics of one site. Never contains key values or a hash of key values (SEC-RF107-09).</summary>
public sealed record RowFilterKeySetOutcome(
    string Table,
    Guid? RuleId,
    CorrelatedFilterSource Source,
    RowFilterEvaluationSource StrategySource,
    KeySetOutcomeKind Outcome,
    KeySetFallbackReason? Reason,
    int? KeyCount,
    int? PaddedCount,
    int? AgeSeconds);

/// <summary>Per-statement collector of <see cref="RowFilterKeySetOutcome"/> (single statement, single assembly; not thread-safe).</summary>
public sealed class RowFilterKeySetReport
{
    private readonly List<RowFilterKeySetOutcome> _outcomes = [];

    public IReadOnlyList<RowFilterKeySetOutcome> Outcomes => _outcomes;

    public void Add(RowFilterKeySetOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _outcomes.Add(outcome);
    }
}

/// <summary>Result of <c>GovernancePolicyFactory.CreateAssemblyAsync</c>: the policy plus the key-set diagnostics.</summary>
public sealed record GovernedPolicyAssembly(GovernancePolicy Policy, RowFilterKeySetReport KeySets);

/// <summary>
/// Cached key set of one correlated filter. <see cref="Tenant"/> and <see cref="DataSource"/> are verified on every hit (SEC-RF107-02).
/// <see cref="ToString"/> prints counts only (SEC-RF107-09).
/// </summary>
public sealed record RowFilterKeySetEntry(
    IReadOnlyList<object> Keys,
    bool Overflow,
    DateTimeOffset LoadedAt,
    string Tenant,
    string DataSource)
{
    public long EstimateBytes() => throw new NotImplementedException("WP-C: RowFilterKeySetEntry.EstimateBytes");

    public override string ToString() => $"RowFilterKeySetEntry {{ Keys = {Keys.Count}, Overflow = {Overflow}, LoadedAt = {LoadedAt:O} }}";
}

public static class RowFilterKeySetErrorCodes
{
    /// <summary>The key query failed (connection, command, read, timeout, conversion); the statement is denied, fail closed (SEC-RF107-08).</summary>
    public const string KeySetUnavailable = "ROW_FILTER_KEY_SET_UNAVAILABLE";
}
