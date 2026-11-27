namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HotChocolate.Execution;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

/// <summary>
/// Request middleware for the opt-in <c>extensions.autheris.roots</c> response extension: per root field the dataset,
/// its tags, whether a row filter of the data owner or consent applied, whether the result was cut and, for aggregates,
/// the privacy facts the caller can select anyway. Without <c>extensions.autheris.datasetFacts: true</c> in the request
/// nothing is collected and the response is unchanged.
/// </summary>
public sealed class DatasetFactsMiddleware
{
    private readonly RequestDelegate _next;

    public DatasetFactsMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        DatasetFactsCollector? collector = null;
        if (DatasetFactsRequest.IsRequested(context.Request))
        {
            collector = new DatasetFactsCollector();
            context.ContextData[DatasetFactsCollector.ContextKey] = collector;
        }

        await _next(context).ConfigureAwait(false);

        if (collector is null || context.Result is not OperationResult opResult || opResult.Data is not { IsValueNull: false })
        {
            return;
        }

        var failedRoots = new HashSet<string>(StringComparer.Ordinal);
        if (opResult.Errors is { Count: > 0 } errors)
        {
            foreach (var error in errors)
            {
                var segments = error.Path?.ToList();
                if (segments is null || segments.Count == 0 || segments[0] is not string first)
                {
                    // An error without a path cannot be tied to a root: report nothing.
                    return;
                }

                failedRoots.Add(first);
            }
        }

        var roots = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in collector.Roots.Keys.Where(n => !failedRoots.Contains(n)).Order(StringComparer.Ordinal))
        {
            roots[name] = ToEntry(collector.Roots[name]);
        }

        var extensions = opResult.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
        var autheris = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (extensions.TryGetValue("autheris", out var existing))
        {
            if (existing is IReadOnlyDictionary<string, object?> readOnly)
            {
                foreach (var kvp in readOnly)
                {
                    autheris[kvp.Key] = kvp.Value;
                }
            }
            else if (existing is IDictionary<string, object?> dict)
            {
                foreach (var kvp in dict)
                {
                    autheris[kvp.Key] = kvp.Value;
                }
            }
        }

        autheris["roots"] = roots;
        opResult.Extensions = extensions.SetItem("autheris", autheris);
    }

    private static Dictionary<string, object?> ToEntry(DatasetFactsCollector.RootFacts root)
    {
        var entry = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddFacts(entry, root.Facts);
        entry["truncated"] = root.Truncated;
        if (root.Aggregate is { } aggregate)
        {
            if (aggregate.SuppressedGroupsCount is { } suppressed)
            {
                entry["suppressedGroupsCount"] = suppressed;
            }

            entry["minimumGroupSize"] = aggregate.MinimumGroupSize;
            entry["mergedIntoOther"] = aggregate.MergedIntoOther;
        }
        else
        {
            var relations = new List<object?>();
            foreach (var relation in root.Relations ?? [])
            {
                var item = new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = relation.Path };
                AddFacts(item, relation.Facts);
                relations.Add(item);
            }

            entry["relations"] = relations;
        }

        return entry;
    }

    private static void AddFacts(Dictionary<string, object?> target, DatasetFactsCollector.Facts facts)
    {
        target["dataset"] = facts.Visible.Dataset;
        target["tags"] = new List<object?>(facts.Visible.Tags);
        target["rowFilterApplied"] = facts.RowFilters.Count > 0;
        var filters = new List<object?>();
        foreach (var description in facts.RowFilters.Distinct(StringComparer.Ordinal).Take(2))
        {
            filters.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["description"] = description });
        }

        target["rowFilters"] = filters;
    }
}
