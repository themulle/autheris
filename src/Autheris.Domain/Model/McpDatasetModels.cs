namespace Autheris.Domain.Model;

using System.Collections.Generic;

/// <summary>One catalog table as listed by the MCP <c>list_datasets</c> tool.</summary>
public sealed record McpDatasetSummary(
    string Id,
    string? Description,
    string Sensitivity,
    int ColumnCount);

/// <summary>Result of the MCP <c>list_datasets</c> tool.</summary>
public sealed record McpDatasetList(
    IReadOnlyList<McpDatasetSummary> Datasets,
    int Total,
    bool Truncated);

/// <summary>A column the caller may see, as described by the MCP <c>describe_dataset</c> tool.</summary>
public sealed record McpDatasetColumn(
    string Name,
    string Type,
    string? Description,
    bool Sensitive,
    bool PrimaryKey);

/// <summary>A curated example query (golden query) for a dataset.</summary>
public sealed record McpDatasetExample(
    string Title,
    string? Description,
    string Query,
    string? Variables);

/// <summary>Result of the MCP <c>describe_dataset</c> tool.</summary>
public sealed record McpDatasetDescription(
    string Id,
    string Domain,
    string Schema,
    string Table,
    string? Description,
    string? LongDescription,
    string Sensitivity,
    bool RequiresApproval,
    IReadOnlyList<McpDatasetColumn> Columns,
    IReadOnlyList<McpDatasetExample> Examples);

/// <summary>Result of the MCP <c>sample_rows</c> tool: rows as returned by the governed read path (masked, row filtered).</summary>
public sealed record McpDatasetSample(
    string Id,
    int RowCount,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);
