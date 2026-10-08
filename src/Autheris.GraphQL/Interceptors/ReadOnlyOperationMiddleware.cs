namespace Autheris.GraphQL.Interceptors;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Domain.Security;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

/// <summary>
/// Finding 3.3 / R13: read-only tokens (<see cref="TokenAccessScope"/>) cannot run mutations, also not via MCP tools.
/// </summary>
public sealed class ReadOnlyOperationMiddleware
{
    private readonly RequestDelegate _next;

    public ReadOnlyOperationMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        var document = context.OperationDocumentInfo?.Document;
        if (document != null && IsMutation(document, context.Request.OperationName) && ResolvePrincipal(context).IsReadOnly())
        {
            context.Result = OperationResult.FromError(ErrorBuilder.New()
                .SetMessage("This token only permits read access; mutations are rejected.")
                .SetCode("READ_ONLY_TOKEN")
                .Build());
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    // Without an operation name every mutation in the document counts (fail-closed).
    internal static bool IsMutation(DocumentNode document, string? operationName) =>
        document.Definitions
            .OfType<OperationDefinitionNode>()
            .Where(o => string.IsNullOrEmpty(operationName) || string.Equals(o.Name?.Value, operationName, StringComparison.Ordinal))
            .Any(o => o.Operation == OperationType.Mutation);

    private static ClaimsPrincipal? ResolvePrincipal(RequestContext context)
    {
        if (context.ContextData.TryGetValue(nameof(ClaimsPrincipal), out var value) && value is ClaimsPrincipal principal)
        {
            return principal;
        }

        return context.RequestServices.GetService<IHttpContextAccessor>()?.HttpContext?.User;
    }
}
