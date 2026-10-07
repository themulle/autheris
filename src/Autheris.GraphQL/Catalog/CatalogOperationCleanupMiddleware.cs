namespace Autheris.GraphQL.Catalog;

using System;
using System.Threading.Tasks;
using Autheris.Application.Sql.Tree;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// G3 & R-GQL-3: Ensures that table access memoization and audit sets for the GraphQL operation
/// are deterministically cleaned up when the operation execution completes.
/// </summary>
public sealed class CatalogOperationCleanupMiddleware
{
    private readonly RequestDelegate _next;

    public CatalogOperationCleanupMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            if (context.ContextData.TryGetValue("AutherisOperationId", out var opIdObj) && opIdObj is string opId)
            {
                var treeService = context.RequestServices.GetService<IGovernedTreeQueryService>();
                treeService?.ClearOperation(opId);
            }
        }
    }
}
