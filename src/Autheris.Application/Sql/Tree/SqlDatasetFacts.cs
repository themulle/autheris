using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;

namespace Autheris.Application.Sql.Tree;

/// <summary>One table a read statement used, as the caller may learn about it: dataset id, tags and the fixed row-filter text.</summary>
public sealed record SqlDatasetFact(string Dataset, IReadOnlyList<string> Tags, bool RowFilterApplied, IReadOnlyList<string> RowFilters);

/// <summary>
/// Opt-in facts of a WebSQL read, with the same rules as the GraphQL <c>extensions.autheris.roots</c>: only tables the caller
/// can see in the catalog of the channel get an entry (an invisible table is left out, never named), descriptions are the
/// fixed constants shared with <c>describe_my_access</c>, and neither the statement nor any parameter or value is echoed.
/// <c>RowFilterApplied</c> means "a row filter defined by the data owner or by consent applies to you", not "all rows are
/// returned"; tenant isolation, virtual and mandatory filters are intentionally not reported.
/// </summary>
public static class SqlDatasetFacts
{
    public const int MaxTables = 64;
    internal const int MaxTags = 64;
    internal const int MaxTagLength = 128;

    /// <summary>Resolves the facts; any failure yields no facts (fail closed), cancellation propagates.</summary>
    public static async Task<IReadOnlyList<SqlDatasetFact>> ResolveAsync(
        ICatalogVisibilityService? visibility,
        CatalogCaller caller,
        IReadOnlyList<DatasetAccessFacts>? facts,
        CancellationToken ct)
    {
        if (visibility is null || facts is not { Count: > 0 })
        {
            return [];
        }

        try
        {
            var tables = facts.Select(f => f.Table).Distinct().Take(MaxTables).ToList();
            var batch = await visibility.FindVisibleManyAsync(caller, tables, ct).ConfigureAwait(false);
            var result = new List<SqlDatasetFact>();
            var seen = new HashSet<TableIdentifier>();
            foreach (var fact in facts)
            {
                if (result.Count >= MaxTables || !seen.Add(fact.Table) || !batch.Rows.TryGetValue(fact.Table, out var row))
                {
                    continue;
                }

                result.Add(new SqlDatasetFact(
                    McpDatasetCatalog.DatasetId(row.Table.Identifier),
                    McpDatasetCatalog.BoundedTags(row.Table, MaxTags, MaxTagLength),
                    fact.RowFilterApplied,
                    fact.RowFilterDescriptions.Distinct(StringComparer.Ordinal).Take(2).ToList()));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The response object: <c>{ truncated, tables: [{ dataset, tags, rowFilterApplied, rowFilters: [{ description }] }] }</c>.</summary>
    public static IReadOnlyDictionary<string, object?> ToResponse(IReadOnlyList<SqlDatasetFact> tables, bool truncated)
    {
        var entries = new List<object?>(tables.Count);
        foreach (var table in tables)
        {
            entries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dataset"] = table.Dataset,
                ["tags"] = new List<object?>(table.Tags),
                ["rowFilterApplied"] = table.RowFilterApplied,
                ["rowFilters"] = table.RowFilters
                    .Select(d => (object?)new Dictionary<string, object?>(StringComparer.Ordinal) { ["description"] = d })
                    .ToList(),
            });
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal) { ["truncated"] = truncated, ["tables"] = entries };
    }
}
