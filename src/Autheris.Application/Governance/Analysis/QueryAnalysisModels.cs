namespace Autheris.Application.Governance.Analysis;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Issue #79: Diagnostic codes returned by static query analysis.
/// </summary>
public static class QueryDiagnosticCodes
{
    public const string SyntaxError = "SYNTAX_ERROR";
    public const string UnknownTable = "UNKNOWN_TABLE";
    public const string UnknownColumn = "UNKNOWN_COLUMN";
    public const string MaskedColumnInFilter = "MASKED_COLUMN_IN_FILTER";
    public const string ReferencedTablesMismatch = "REFERENCED_TABLES_MISMATCH";
}

/// <summary>
/// Issue #79: Configuration options for static query cost estimation and mandatory virtual filter checks.
/// </summary>
public sealed class QueryAnalysisOptions
{
    public int MaxAllowedCost { get; init; } = 500;
    public int CostPerRequestedField { get; init; } = 5;
    public int CostPerJoin { get; init; } = 25;
    public int CostPerSubqueryDepth { get; init; } = 30;
    public int UnboundedScanPenalty { get; init; } = 50;

    /// <summary>
    /// Optional map of dataset name (<c>domain.schema.table</c> or <c>table</c>) to mandatory virtual filter column names
    /// that must be supplied by the query (unless the target principal is exempt).
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> MandatoryVirtualFilters { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Issue #79: A single item in a query analysis batch: a GraphQL query (with optional variables),
/// a SQL query, a declared <see cref="SqlEndpointDefinition"/>, or a declared <see cref="ProcedureDefinition"/>.
/// </summary>
public sealed record QueryAnalysisItem(
    string? GraphQlQuery = null,
    IReadOnlyDictionary<string, object?>? Variables = null,
    string? SqlQuery = null,
    string? DataSource = null,
    SqlEndpointDefinition? SqlEndpoint = null,
    ProcedureDefinition? StoredProcedure = null,
    string? ProcedureSqlBody = null,
    string? Name = null)
{
    public static QueryAnalysisItem FromGraphQl(
        string query,
        IReadOnlyDictionary<string, object?>? variables = null,
        string? name = null) =>
        new(GraphQlQuery: query, Variables: variables, Name: name);

    public static QueryAnalysisItem FromSql(
        string sql,
        string? dataSource = null,
        string? name = null) =>
        new(SqlQuery: sql, DataSource: dataSource, Name: name);

    public static QueryAnalysisItem FromSqlEndpoint(
        SqlEndpointDefinition endpoint,
        string? name = null) =>
        new(SqlEndpoint: endpoint, Name: name ?? endpoint.Name);

    public static QueryAnalysisItem FromProcedure(
        ProcedureDefinition procedure,
        string? procedureSqlBody = null,
        string? name = null) =>
        new(StoredProcedure: procedure, ProcedureSqlBody: procedureSqlBody, Name: name ?? procedure.Name);
}

/// <summary>
/// Issue #79: A static validation diagnostic produced during query analysis.
/// </summary>
public sealed record QueryValidationDiagnostic(
    string Code,
    string Message,
    string? QueryName = null,
    string? Dataset = null,
    string? Column = null);

/// <summary>
/// Issue #79: An output column projected by one or more analyzed queries, with its catalog type and effective access level.
/// </summary>
public sealed record AnalyzedOutputColumn(
    string Dataset,
    string Column,
    string DataType,
    ColumnAccessLevel EffectiveAccessLevel)
{
    public bool IsMasked => EffectiveAccessLevel == ColumnAccessLevel.Mask;
}

/// <summary>
/// Issue #79: A deprecated field referenced by an analyzed query.
/// </summary>
public sealed record DeprecatedFieldReference(
    string Dataset,
    string Column,
    DateTimeOffset DeprecatedSince,
    DateTimeOffset SunsetDate,
    string? ReplacementField = null,
    string? Reason = null);

/// <summary>
/// Issue #79: Static query complexity/cost estimate compared against the gateway budget.
/// </summary>
public sealed record QueryCostEstimate(
    int EstimatedCost,
    int MaxAllowedCost,
    bool IsWithinCostLimit);

/// <summary>
/// Issue #79: Output 1 of static query analysis — validation report (syntax, schema, output columns, filter flags, cost, deprecations).
/// Never exposes row-filter SQL predicates or hidden table names.
/// </summary>
public sealed record QueryValidationReport(
    bool IsValid,
    IReadOnlyList<QueryValidationDiagnostic> Diagnostics,
    IReadOnlyList<AnalyzedOutputColumn> OutputColumns,
    bool HasRowFilters,
    bool HasMandatoryVirtualFilters,
    QueryCostEstimate CostEstimate,
    IReadOnlyList<DeprecatedFieldReference> DeprecatedFields,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? PopulatedReferencedTables = null)
{
    public int EstimatedCost => CostEstimate.EstimatedCost;
    public int MaxAllowedCost => CostEstimate.MaxAllowedCost;
    public bool IsWithinCostLimit => CostEstimate.IsWithinCostLimit;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> EffectivePopulatedReferencedTables =>
        PopulatedReferencedTables ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Issue #79: A missing table consent or a masked column for which a cleartext exception can be requested.
/// </summary>
public sealed record RequestableConsentItem(
    string Dataset,
    string? Column,
    string RequestedLevel,
    string SuggestedPurpose);

/// <summary>
/// Issue #79: Output 2 of static query analysis — access requirements report.
/// Hidden tables are never named; they are counted only in <see cref="HiddenMissingDatasetsCount"/>.
/// </summary>
public sealed record QueryAccessRequirementsReport(
    IReadOnlyList<RequestableConsentItem> MissingConsents,
    bool IsBlockedByMandatoryFilter,
    int HiddenMissingDatasetsCount = 0,
    string? HiddenDatasetsHint = null)
{
    public bool IsFullyAuthorized =>
        MissingConsents.Count == 0 &&
        HiddenMissingDatasetsCount == 0 &&
        !IsBlockedByMandatoryFilter;

    public IReadOnlyList<RequestableConsentItem> MissingTableGrants =>
        MissingConsents.Where(m => m.Column == null).ToList();

    public IReadOnlyList<RequestableConsentItem> MaskedColumnExceptions =>
        MissingConsents.Where(m => m.Column != null).ToList();
}

/// <summary>
/// Issue #79: Combined result of zero-read static query analysis.
/// </summary>
public sealed record QueryAnalysisResult(
    QueryValidationReport ValidationReport,
    QueryAccessRequirementsReport AccessRequirements,
    int SourceDatabaseReadsExecuted = 0);

/// <summary>
/// Issue #79: Request to submit a bundled multi-table consent request derived from an access requirements report.
/// </summary>
public sealed record BundledConsentRequest(
    IReadOnlyList<RequestableConsentItem> Items,
    string Purpose,
    string? ConsumerReference = null,
    DateTimeOffset? RequestedUntilUtc = null);

/// <summary>
/// Issue #79: One per-Data-Owner consent approval ticket created by <see cref="BundledConsentRequestService"/>.
/// </summary>
public sealed record OwnerConsentTicket(
    Guid TicketId,
    string DataOwnerKey,
    string? DataOwnerDisplay,
    IReadOnlyList<string> Datasets,
    IReadOnlyList<RequestableConsentItem> Items,
    string? ConsumerReference,
    string Status,
    IReadOnlyList<Guid> UnderlyingConsentRequestIds);

/// <summary>
/// Issue #79: Result of submitting a bundled multi-table consent request split by Data Owner.
/// </summary>
public sealed record BundledConsentRequestResult(
    IReadOnlyList<OwnerConsentTicket> Tickets,
    string? ConsumerReference);

/// <summary>
/// Issue #79: Aggregate grant gap count for a single dataset across a group of principals (never exposes individual identities).
/// </summary>
public sealed record DatasetGrantGapSummary(
    string Dataset,
    int MembersMissingGrantCount,
    int TotalMembersEvaluated);
