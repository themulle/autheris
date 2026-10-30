namespace Autheris.Domain.Model;

using System.Linq;
using System.Collections.Generic;
using Autheris.Domain.Common;

/// <summary>One catalog table as listed by the MCP <c>list_datasets</c> tool.</summary>
/// <param name="GraphQlField">Root query field of the dataset in the GraphQL API; null when the dataset is not in the GraphQL schema.</param>
public sealed record McpDatasetSummary(
    string Id,
    string DisplayName,
    string? Description,
    string Sensitivity,
    int ColumnCount,
    string? GraphQlField = null,
    IReadOnlyList<string>? Tags = null,
    string? AccessStatus = null,
    string Kind = "table");

/// <summary>Issue #153: Parameter information for a stored procedure exposed via describe_dataset.</summary>
public sealed record McpProcedureParameterInfo(
    string Name,
    string SqlType,
    string ClrType,
    bool IsRequired,
    string? Description = null);

/// <summary>A way to reach the gateway's data: protocol, HTTP method and URL. <see cref="Preferred"/> marks GraphQL.</summary>
public sealed record McpAccessPath(
    string Protocol,
    string Method,
    string Url,
    string Description,
    bool Preferred = false);

/// <summary>Summary count of tables in a business domain for high-level token-efficient catalog exploration.</summary>
public sealed record McpDomainCount(string Domain, int TableCount);

/// <summary>Result of the MCP <c>list_datasets</c> tool.</summary>
/// <param name="Guidance">How to query data: GraphQL first.</param>
/// <param name="Endpoints">All enabled protocols of the gateway, GraphQL first.</param>
public sealed record McpDatasetList(
    IReadOnlyList<McpDatasetSummary> Datasets,
    int Total,
    bool Truncated,
    string? Guidance = null,
    IReadOnlyList<McpAccessPath>? Endpoints = null,
    int Offset = 0,
    int Limit = 50,
    int? NextOffset = null,
    string? Language = null,
    string? RequestedLanguage = null,
    IReadOnlyList<McpDomainCount>? Domains = null);

/// <summary>Issue #79: Field sunsetting and deprecation metadata exposed in <c>describe_dataset</c> and query analysis.</summary>
public sealed record FieldDeprecationInfo(
    System.DateTimeOffset DeprecatedSince,
    System.DateTimeOffset SunsetDate,
    string? ReplacementField = null,
    string? Reason = null);

/// <summary>Issue #80: Coarsened value profile of a column exposed in <c>describe_dataset</c> without querying the data source.</summary>
public sealed record ColumnValueProfile(
    string? FormatPattern = null,
    string? MinValue = null,
    string? MaxValue = null,
    double EmptyShare = 0.0,
    int? TypicalLength = null,
    long? ApproximateDistinctCount = null,
    IReadOnlyList<string>? AllowedValues = null,
    string? DistinctCountBucket = null,
    IReadOnlyList<string>? SampleValues = null)
{
    public string? Min => MinValue;
    public string? Max => MaxValue;
}

/// <summary>A column the caller may see, as described by the MCP <c>describe_dataset</c> tool.</summary>
/// <param name="Type">Data type in the source database.</param>
/// <param name="GraphQlField">Field name in the GraphQL API (may differ from the column name).</param>
/// <param name="GraphQlType">GraphQL scalar of the field (String, Int, Long, Float, Decimal, Boolean).</param>
public sealed record McpDatasetColumn(
    string Name,
    string Type,
    string? Description,
    bool Sensitive,
    bool PrimaryKey,
    string? GraphQlField = null,
    string? GraphQlType = null,
    ColumnSemantics? Semantics = null,
    FieldDeprecationInfo? Deprecation = null,
    ColumnValueProfile? ValueProfile = null,
    string? Access = null,
    string? DisplayName = null);

/// <summary>A navigation from a dataset to a related dataset in the GraphQL API.</summary>
public sealed record McpGraphQlRelation(string Field, string Dataset, bool IsList);

/// <summary>How to query a dataset through GraphQL, the preferred query path.</summary>
public sealed record McpGraphQlAccess(
    string Endpoint,
    string QueryField,
    string TypeName,
    string FilterType,
    string OrderByType,
    string Arguments,
    string ExampleQuery,
    IReadOnlyList<McpGraphQlRelation> Relations);

/// <summary>A curated example query (golden query) for a dataset.</summary>
public sealed record McpDatasetExample(
    string Title,
    string? Description,
    string Query,
    string? Variables);

/// <summary>Result of the MCP <c>describe_dataset</c> tool.</summary>
public sealed record McpDatasetDescription(
    string Id,
    string DisplayName,
    string Domain,
    string Schema,
    string Table,
    string? Description,
    string? LongDescription,
    string Sensitivity,
    bool RequiresApproval,
    IReadOnlyList<McpDatasetColumn> Columns,
    IReadOnlyList<McpDatasetExample> Examples,
    McpGraphQlAccess? GraphQl = null,
    IReadOnlyList<McpAccessPath>? OtherAccess = null,
    IReadOnlyList<string>? Tags = null,
    string? DataOwner = null,
    IReadOnlyList<string>? DataOwnerDelegates = null,
    string? AccessStatus = null,
    IReadOnlyList<TableIndexDefinition>? Indexes = null,
    string? View = null,
    string Kind = "table",
    IReadOnlyList<McpProcedureParameterInfo>? Parameters = null,
    IReadOnlyList<string>? RequiredFilterColumns = null,
    IReadOnlyList<IReadOnlyList<string>>? RequiredFilterColumnGroups = null,
    string? Language = null,
    string? RequestedLanguage = null,
    IReadOnlyList<McpKeyColumn>? PrimaryKeyOrder = null);

/// <summary>Issue #390: one primary key column in key order with its declared sort direction (<c>ASC</c> or <c>DESC</c>).</summary>
public sealed record McpKeyColumn(string Column, string Direction)
{
    /// <summary>The key order of an already restricted view (I390-SC-11: only a key wholly visible to the caller has columns left); null when there is none.</summary>
    public static IReadOnlyList<McpKeyColumn>? FromKey(TableMetadata restricted) =>
        restricted.PrimaryKeyColumns.Count == 0
            ? null
            : restricted.PrimaryKeyColumns
                .Select((c, i) => new McpKeyColumn(c, KeyDirections.IsDescending(restricted.PrimaryKeyDirections, i) ? "DESC" : "ASC"))
                .ToList();
}

/// <summary>GraphQL names of one catalog table, as generated by the catalog GraphQL schema.</summary>
public sealed record GraphQlTableMapping(
    TableIdentifier Table,
    string QueryField,
    string TypeName,
    string FilterType,
    string OrderByType,
    IReadOnlyList<GraphQlColumnMapping> Columns,
    IReadOnlyList<GraphQlRelationMapping> Relations);

public sealed record GraphQlColumnMapping(string ColumnName, string Field, string GraphQlType);

public sealed record GraphQlRelationMapping(string Field, TableIdentifier Target, bool IsList);

/// <summary>Result of the MCP <c>sample_rows</c> tool: rows as returned by the governed read path (masked, row filtered) or synthetic generator.</summary>
public sealed record McpDatasetSample(
    string Id,
    int RowCount,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool IsSynthetic = false);

/// <summary>Token-efficient column summary for AI code and query generation.</summary>
public sealed record McpTableBriefColumn(
    string Name,
    string Type,
    string? Description = null,
    bool PrimaryKey = false,
    bool IsSensitive = false);

/// <summary>Join relationship between datasets for automated query/report construction.</summary>
public sealed record McpTableRelation(
    string TargetTable,
    string FromColumn,
    string ToColumn);

/// <summary>Token-efficient schema description of a single dataset.</summary>
public sealed record McpTableBriefSchema(
    string Dataset,
    string Domain,
    string? Description,
    IReadOnlyList<string> PrimaryKeys,
    IReadOnlyList<McpTableBriefColumn> Columns,
    IReadOnlyList<McpTableRelation> Relations,
    string? AccessStatus = null);

/// <summary>Result of get_table_schema.</summary>
public sealed record McpTableSchemaResult(
    int TotalTables,
    IReadOnlyList<McpTableBriefSchema> Tables);

/// <summary>Candidate table for an AI report or app generation use case.</summary>
public sealed record McpUseCaseCandidate(
    string Dataset,
    string Domain,
    string? Description,
    double RelevanceScore,
    IReadOnlyList<string> MatchedColumns,
    IReadOnlyList<McpTableRelation> Relations);

/// <summary>Result of find_relevant_tables.</summary>
public sealed record McpUseCaseDiscoveryResult(
    string UseCase,
    int TotalCandidates,
    IReadOnlyList<McpUseCaseCandidate> Candidates,
    string? RecommendedNextStep = null);
