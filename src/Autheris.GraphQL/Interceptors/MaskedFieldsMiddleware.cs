namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HotChocolate.Execution;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

/// <summary>
/// Hot Chocolate request pipeline middleware that attaches extensions.maskedFields containing
/// the distinct list of field paths that underwent column-level masking during query execution.
/// </summary>
public sealed class MaskedFieldsMiddleware
{
    public const string ContextKey = "MaskedFields";
    private readonly RequestDelegate _next;

    public MaskedFieldsMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        var bag = new ConcurrentBag<string>();
        context.ContextData[ContextKey] = bag;

        await _next(context).ConfigureAwait(false);

        if (!bag.IsEmpty && context.Result is OperationResult opResult)
        {
            var distinctList = bag.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var extensions = opResult.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
            opResult.Extensions = extensions.SetItem("maskedFields", distinctList);
        }
    }
}
