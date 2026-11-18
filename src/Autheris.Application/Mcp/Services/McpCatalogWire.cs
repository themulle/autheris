namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Autheris.Application.Catalog.Discovery;
using Autheris.Domain.Model;

/// <summary>
/// Issue #799: the token-saving wire shapes of <c>describe_dataset(brief: true)</c> and <c>list_datasets(format: "compact")</c>. Both are
/// projections of an already authorized result; they only remove members or shorten text and never add information.
/// </summary>
public static class McpCatalogWire
{
    /// <summary>Most characters (runes) of a dataset description in a compact list entry: the first sentence, cut when longer.</summary>
    public const int CompactDescriptionRunes = 120;

    /// <summary>
    /// The object the MCP executor serializes for a dataset tool result: the brief describe shape for a brief description, the compact list
    /// shape when <paramref name="compactList"/> is set, otherwise <paramref name="result"/> unchanged.
    /// </summary>
    public static object ForTool(object result, bool compactList) => result switch
    {
        McpDatasetDescription { View: McpDatasetCatalog.BriefView } brief => Brief(brief),
        McpDatasetList list when compactList => Compact(list),
        _ => result
    };

    /// <summary>Column signatures (name, type, access, key flag), the key in order, indexes and required filters; no per-column prose.</summary>
    public static McpBriefDatasetDescription Brief(McpDatasetDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        var columns = new McpColumnSignature[description.Columns.Count];
        for (var i = 0; i < columns.Length; i++)
        {
            var c = description.Columns[i];
            columns[i] = new McpColumnSignature(c.Name, c.Type, c.Access, c.PrimaryKey ? true : null);
        }

        return new McpBriefDatasetDescription(
            description.Id,
            description.DisplayName,
            description.Description,
            description.Sensitivity,
            description.RequiresApproval,
            columns,
            description.ColumnTotal,
            description.AccessStatus,
            description.PrimaryKeyOrder,
            description.Indexes is { Count: > 0 } indexes ? indexes : null,
            description.RequiredFilterColumns,
            description.RequiredFilterColumnGroups,
            McpDatasetCatalog.BriefView,
            string.Equals(description.Kind, "table", StringComparison.Ordinal) ? null : description.Kind,
            description.Language,
            description.RequestedLanguage,
            description.Size);
    }

    /// <summary>Id, display name (when it differs from the table name), first-sentence description, sensitivity and access status.</summary>
    public static McpCompactDatasetList Compact(McpDatasetList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var datasets = new McpCompactDatasetSummary[list.Datasets.Count];
        for (var i = 0; i < datasets.Length; i++)
        {
            var d = list.Datasets[i];
            datasets[i] = new McpCompactDatasetSummary(
                d.Id,
                d.DisplayName.AsSpan().Equals(TableNameOf(d.Id), StringComparison.OrdinalIgnoreCase) ? null : d.DisplayName,
                DiscoveryTextSanitizer.Summarize(d.Description, CompactDescriptionRunes),
                d.Sensitivity,
                d.AccessStatus,
                string.Equals(d.Kind, "table", StringComparison.Ordinal) ? null : d.Kind);
        }

        return new McpCompactDatasetList(
            datasets,
            list.Total,
            list.Truncated,
            list.Guidance,
            list.Offset,
            list.Limit,
            list.NextOffset,
            list.Language,
            list.RequestedLanguage,
            list.Domains);
    }

    private static ReadOnlySpan<char> TableNameOf(string id)
    {
        var dot = id.AsSpan().LastIndexOf('.');
        var colon = id.AsSpan().LastIndexOf(':');
        return id.AsSpan(Math.Max(dot, colon) + 1);
    }
}

/// <summary>Issue #799: one column of <c>describe_dataset(brief: true)</c>; <c>primaryKey</c> is written only for key columns.</summary>
public sealed record McpColumnSignature(string Name, string Type, string? Access = null, bool? PrimaryKey = null);

/// <summary>Issue #799: the <c>describe_dataset(brief: true)</c> answer. <c>kind</c> is written only when it is not <c>table</c>.</summary>
public sealed record McpBriefDatasetDescription(
    string Id,
    string DisplayName,
    string? Description,
    string Sensitivity,
    bool RequiresApproval,
    IReadOnlyList<McpColumnSignature> Columns,
    int? ColumnTotal,
    string? AccessStatus,
    IReadOnlyList<McpKeyColumn>? PrimaryKeyOrder,
    IReadOnlyList<DatasetIndexInfo>? Indexes,
    IReadOnlyList<string>? RequiredFilterColumns,
    IReadOnlyList<IReadOnlyList<string>>? RequiredFilterColumnGroups,
    string View,
    string? Kind,
    string? Language,
    string? RequestedLanguage,
    McpDatasetSize? Size);

/// <summary>Issue #799: one entry of <c>list_datasets(format: "compact")</c>.</summary>
public sealed record McpCompactDatasetSummary(
    string Id,
    string? DisplayName,
    string? Description,
    string Sensitivity,
    string? AccessStatus,
    string? Kind);

/// <summary>Issue #799: the <c>list_datasets(format: "compact")</c> answer; <c>domains</c> is always written, as in the full list.</summary>
public sealed record McpCompactDatasetList(
    IReadOnlyList<McpCompactDatasetSummary> Datasets,
    int Total,
    bool Truncated,
    string? Guidance,
    int Offset,
    int Limit,
    int? NextOffset,
    string? Language,
    string? RequestedLanguage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<McpDomainCount>? Domains);
