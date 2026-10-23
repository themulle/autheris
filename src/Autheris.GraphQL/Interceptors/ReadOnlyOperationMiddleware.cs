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
        if (document != null && IsSubscription(document, context.Request.OperationName)
            && ResolvePrincipal(context).IsReadOnly())
        {
            // Issue #279: read-only tokens never open subscriptions; no self-service branch, no options lookup.
            context.Result = Reject("This token only permits read access; subscriptions are rejected.");
            return;
        }

        if (document != null && IsMutation(document, context.Request.OperationName))
        {
            var principal = ResolvePrincipal(context);
            if (principal.IsReadOnly())
            {
                var gatewayOptions = context.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>>()?.Value;
                var entraId = gatewayOptions?.Authentication?.EntraId;
                var selfServiceEnabled = entraId?.SelfServiceEnabled ?? true;
                var configuredScopes = entraId?.ReadOnlySelfServiceScopes;

                if (!selfServiceEnabled || configuredScopes is { Count: 0 })
                {
                    context.Result = Reject(MutationRejected);
                    return;
                }

                var selfServiceScopes = configuredScopes is { Count: > 0 } ? configuredScopes : ["data.access.request"];
                var hasSelfServiceScope = selfServiceScopes.Any(s => principal?.HasScope(s) == true);

                if (!hasSelfServiceScope || !SelfServiceMutationClassifier.IsExclusivelySelfServiceMutation(document, context.Request.OperationName))
                {
                    context.Result = Reject(MutationRejected);
                    return;
                }
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private const string MutationRejected = "This token only permits read access; mutations are rejected.";

    private static OperationResult Reject(string message) =>
        OperationResult.FromError(ErrorBuilder.New().SetMessage(message).SetCode("READ_ONLY_TOKEN").Build());

    internal static bool IsMutation(DocumentNode document, string? operationName) =>
        SelectsOperation(document, operationName, OperationType.Mutation);

    internal static bool IsSubscription(DocumentNode document, string? operationName) =>
        SelectsOperation(document, operationName, OperationType.Subscription);

    // Without an operation name every operation of the type in the document counts (fail-closed).
    internal static bool SelectsOperation(DocumentNode document, string? operationName, OperationType type) =>
        document.Definitions
            .OfType<OperationDefinitionNode>()
            .Where(o => string.IsNullOrEmpty(operationName) || string.Equals(o.Name?.Value, operationName, StringComparison.Ordinal))
            .Any(o => o.Operation == type);

    private static ClaimsPrincipal? ResolvePrincipal(RequestContext context)
    {
        if (context.ContextData.TryGetValue(nameof(ClaimsPrincipal), out var value) && value is ClaimsPrincipal principal)
        {
            return principal;
        }

        return context.RequestServices.GetService<IHttpContextAccessor>()?.HttpContext?.User;
    }
}
