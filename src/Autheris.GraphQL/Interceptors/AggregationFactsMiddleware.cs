namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HotChocolate.Execution;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

/// <summary>The result facts of one flat aggregation root field, copied from the governed service result.</summary>
internal sealed record AggregationFacts(
    long Count,
    bool Suppressed,
    int SuppressedGroupsCount,
    int MinimumGroupSize,
    string MinimumGroupSizeSource,
    bool MergedIntoOther,
    bool Truncated);

/// <summary>
/// Request middleware that installs a per-request bag for the facts of flat aggregation fields and, after execution, writes them to
/// <c>extensions.autherisAggregation.&lt;responseKey&gt;</c>. Without the bag a flat field fails closed.
/// </summary>
public sealed class AggregationFactsMiddleware
{
    public const string ContextKey = "AutherisAggregationFacts";
    public const string ExtensionName = "autherisAggregation";
    private readonly RequestDelegate _next;

    public AggregationFactsMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        var bag = new ConcurrentDictionary<string, AggregationFacts>(StringComparer.Ordinal);
        context.ContextData[ContextKey] = bag;

        await _next(context).ConfigureAwait(false);

        if (!bag.IsEmpty && context.Result is OperationResult opResult)
        {
            var facts = new Dictionary<string, object?>(bag.Count, StringComparer.Ordinal);
            foreach (var (key, value) in bag.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                facts[key] = new Dictionary<string, object?>(7, StringComparer.Ordinal)
                {
                    ["count"] = value.Count,
                    ["suppressed"] = value.Suppressed,
                    ["suppressedGroupsCount"] = value.SuppressedGroupsCount,
                    ["minimumGroupSize"] = value.MinimumGroupSize,
                    ["minimumGroupSizeSource"] = value.MinimumGroupSizeSource,
                    ["mergedIntoOther"] = value.MergedIntoOther,
                    ["truncated"] = value.Truncated
                };
            }

            var extensions = opResult.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
            opResult.Extensions = extensions.SetItem(ExtensionName, facts);
        }
    }
}
