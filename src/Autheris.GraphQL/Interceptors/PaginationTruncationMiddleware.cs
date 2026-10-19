namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HotChocolate.Execution;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

/// <summary>
/// Hot Chocolate request pipeline middleware that attaches extensions.pagination metadata,
/// signaling whether results were truncated due to row limits or cost limits (Issue #39).
/// </summary>
public sealed class PaginationTruncationMiddleware
{
    public const string ContextKey = "PaginationTruncated";
    public const string MaxRowsReachedKey = "PaginationMaxRowsReached";
    public const string PageSizeKey = "PaginationPageSize";
    private readonly RequestDelegate _next;

    public PaginationTruncationMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        await _next(context).ConfigureAwait(false);

        if (context.Result is OperationResult opResult &&
            context.ContextData.TryGetValue(ContextKey, out var truncatedObj) &&
            truncatedObj is true)
        {
            var maxRowsReached = context.ContextData.TryGetValue(MaxRowsReachedKey, out var maxObj) && maxObj is true;
            var pageSize = context.ContextData.TryGetValue(PageSizeKey, out var sizeObj) && sizeObj is int sz ? sz : 0;

            var paginationInfo = new Dictionary<string, object?>
            {
                ["isTruncated"] = true,
                ["maxRowsReached"] = maxRowsReached,
                ["pageSize"] = pageSize
            };

            var extensions = opResult.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
            opResult.Extensions = extensions.SetItem("pagination", paginationInfo);
        }
    }
}
