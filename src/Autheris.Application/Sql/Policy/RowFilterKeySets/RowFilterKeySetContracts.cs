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

    /// <summary>
    /// D-WPC-2 / SEC-RF107-17: the statement is not a read statement, so <see cref="ApplyAsync"/> is not invoked. Reports <paramref name="reason"/> for every
    /// site whose resolved strategy is <c>KeySetCache</c>; never reads epochs, the cache or the loader.
    /// </summary>
    void ReportSkipped(
        RowFilterKeySetContext context,
        IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates,
        RowFilterKeySetReport report,
        KeySetFallbackReason reason);
}

/// <param name="GovernanceIdentifiers">Target and dependent governance identifiers per table (the epoch keys).</param>
/// <param name="Disabled">True on the single bind-limit retry (SEC-RF107-15): every site stays <c>EXISTS</c>.</param>
/// <param name="IsRead">D-WPC-1 / SEC-RF107-17: <c>metadata.StatementType == Select</c>.</param>
/// <param name="UserSqlLength">D-WPC-1: length of the user statement (text budget, D-WPC-3).</param>
/// <param name="MaxExpansionFactor">D-WPC-1: the normalized expansion factor of the compile request.</param>
public sealed record RowFilterKeySetContext(
    string DataSourceName,
    TenantId Tenant,
    TargetSqlDialect Dialect,
    ITableCatalog Catalog,
    TenantBinding TenantBinding,
    IReadOnlyDictionary<TableIdentity, TableMetadata?> Metadata,
    IReadOnlyDictionary<TableIdentity, IReadOnlyList<TableIdentifier>> GovernanceIdentifiers,
    bool Disabled,
    bool IsRead = true,
    int UserSqlLength = 0,
    int MaxExpansionFactor = CompileRequestFactory.DefaultMaxExpansionFactor);

public enum KeySetOutcomeKind { Exists, Hit, Miss, Fallback }

/// <summary><c>Deny_Negated</c> is the reason of a site in a negated context, <c>Deny_Shared</c> the one of a node instance that occurs more than once in the predicate (SEC-RF107-06).</summary>
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
    LoadThrottled,

    /// <summary>D-WPC-3: the IN lists would not fit the expansion budget of the statement text (pre-load gate or post-load fit).</summary>
    ExpansionBudget,

    /// <summary>D-WPC-4: the compile hit the expansion limit and the one-time retry re-assembled the statement with every site as <c>EXISTS</c>.</summary>
    ExpansionLimitRetry,

    /// <summary>D-WPC-2: key sets apply to read statements only; INSERT, UPDATE, DELETE and unknown statement types keep <c>EXISTS</c>.</summary>
    WriteStatement,

    /// <summary>SEC-RF107-06: the node instance of the site occurs more than once in the predicate tree and is never substituted.</summary>
    Deny_Shared
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


/// <summary>Result of <c>GovernancePolicyFactory.CreateAssemblyAsync</c>: the policy plus the key-set diagnostics.</summary>
public sealed record GovernedPolicyAssembly(GovernancePolicy Policy, RowFilterKeySetReport KeySets);
