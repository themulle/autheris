namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.DataSources;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options.DataSources;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Execution.Processing;
using Microsoft.Extensions.DependencyInjection;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

/// <summary>
/// Issue #447: the data sources that every root field of the compiled operation reads (across Query, Mutation, Subscription,
/// aliases, fragments, federation entity roots, and variable-selected fields resolved by the operation compiler) are checked
/// all-or-nothing before any resolver runs. One denied source denies the whole operation: a request-level error and no
/// <c>data</c>, so no resolver result or side effect precedes the denial.
/// </summary>
public sealed class TransportGateMiddleware
{
    private const string FederationEntitiesFieldName = "_entities";

    /// <summary>Context-data key (global state) of the request scope's ingress transport.</summary>
    public const string IngressContextKey = "AutherisIngressTransport";

    private readonly RequestDelegate _next;

    public TransportGateMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        // HotChocolate may hand a resolver another service scope than the request scope that carries the stamped ingress:
        // the request value travels in the request's context data, and TransportIngressFieldMiddleware copies it into the resolver scope.
        if (context.RequestServices.GetService<IIngressTransportContext>() is { Current: not DataSourceTransportFlags.None } requestIngress)
        {
            context.ContextData[IngressContextKey] = requestIngress.Current;
        }

        // Fail closed when the document or compiled operation is absent (e.g., unknown operationName or uncompiled pipeline stage).
        if (context.OperationDocumentInfo?.Document is null ||
            !context.TryGetOperation(out var operation) ||
            operation is null)
        {
            context.Result = DeniedResult();
            return;
        }

        var sources = new List<string>();
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        if (!CollectRootSources(context, operation, sources, seenSources))
        {
            context.Result = DeniedResult();
            return;
        }

        if (sources.Count > 0)
        {
            // Fail-closed: a scope without the gate (a host that did not register it) denies, it never skips the check.
            var gate = context.RequestServices.GetService<IDataSourceTransportGate>() ?? DenyAllTransportGate.Instance;
            try
            {
                await gate.EnsureCatalogSourcesAsync(sources, context.RequestAborted).ConfigureAwait(false);
            }
            catch (TransportNotAllowedException)
            {
                context.Result = DeniedResult();
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool CollectRootSources(
        RequestContext context,
        Operation operation,
        List<string> sources,
        HashSet<string> seenSources)
    {
        foreach (var selection in operation.RootSelectionSet.Selections)
        {
            if (!IsSelectionIncluded(context, operation, selection))
            {
                continue;
            }

            var fieldFeature = selection.Field.Features.Get<TransportSourceFeature>();
            if (fieldFeature is not null && seenSources.Add(fieldFeature.LogicalSource))
            {
                sources.Add(fieldFeature.LogicalSource);
            }

            if (string.Equals(selection.Field.Name, FederationEntitiesFieldName, StringComparison.Ordinal) &&
                !CollectFederationEntitySources(context, operation, selection, fieldFeature is not null, sources, seenSources))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CollectFederationEntitySources(
        RequestContext context,
        Operation operation,
        Selection selection,
        bool hasFieldFeature,
        List<string> sources,
        HashSet<string> seenSources)
    {
        if (!selection.HasSelections)
        {
            return hasFieldFeature;
        }

        var matchedAnyEntityType = false;
        foreach (var possibleType in operation.GetPossibleTypes(selection))
        {
            var entitySelections = operation.GetSelectionSet(selection, possibleType).Selections;
            if (!HasIncludedSelection(context, operation, entitySelections))
            {
                continue;
            }

            matchedAnyEntityType = true;
            if (possibleType.Features.Get<TransportSourceFeature>() is { } entityFeature)
            {
                if (seenSources.Add(entityFeature.LogicalSource))
                {
                    sources.Add(entityFeature.LogicalSource);
                }
            }
            else if (!hasFieldFeature)
            {
                // Fail closed: catalog/federation entity roots without TransportSourceFeature must not bypass transport checks.
                return false;
            }
        }

        return matchedAnyEntityType || hasFieldFeature;
    }

    private static bool HasIncludedSelection(
        RequestContext context,
        Operation operation,
        ReadOnlySpan<Selection> selections)
    {
        foreach (var selection in selections)
        {
            if (IsSelectionIncluded(context, operation, selection))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSelectionIncluded(
        RequestContext context,
        Operation operation,
        Selection selection)
    {
        if (!selection.IsConditional)
        {
            return true;
        }

        var variables = context.VariableValues;
        if (variables.IsDefaultOrEmpty)
        {
            // Conditional selection without coerced variables: evaluate conservatively so checks stay fail-closed.
            return true;
        }

        foreach (var variableSet in variables)
        {
            if (selection.IsIncluded(operation.CreateIncludeConditionFlags(variableSet)))
            {
                return true;
            }
        }

        return false;
    }

    private static OperationResult DeniedResult() =>
        // Fixed text and code: no source name, no sub-reason; the audit event was written by the gate.
        OperationResult.FromError(
            ErrorBuilder.New()
                .SetMessage("Access to the data source is not allowed over this transport.")
                .SetCode(TransportNotAllowedException.Code)
                .Build());
}

/// <summary>Feature on a catalog root field or entity type that names its logical data source (read by <see cref="TransportGateMiddleware"/>).</summary>
public sealed record TransportSourceFeature(string LogicalSource);
