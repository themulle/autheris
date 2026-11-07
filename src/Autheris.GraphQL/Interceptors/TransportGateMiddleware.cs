namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.DataSources;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options.DataSources;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

/// <summary>
/// Issue #447: the data sources that every root field of the compiled operation reads (all aliases, fragments and
/// variable-selected fields are already flattened by the operation compiler) are checked all-or-nothing before any resolver runs.
/// One denied source denies the whole operation: a request-level error and no <c>data</c>, so no resolver result or side effect
/// precedes the denial. The per-table gate at C3/C1 stays in place as the second line (subscriptions and nested access).
/// </summary>
public sealed class TransportGateMiddleware
{
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

        var document = context.OperationDocumentInfo?.Document;
        var sources = new List<string>();
        if (document is not null && context.Schema?.QueryType is { } queryType)
        {
            foreach (var rootField in RootFieldNames(document, context.Request.OperationName))
            {
                if (queryType.Fields.TryGetField(rootField, out var field) &&
                    field.Features.Get<TransportSourceFeature>() is { } feature)
                {
                    sources.Add(feature.LogicalSource);
                }
            }
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
                // Fixed text and code: no source name, no sub-reason; the audit event was written by the gate.
                context.Result = OperationResult.FromError(
                    ErrorBuilder.New()
                        .SetMessage("Access to the data source is not allowed over this transport.")
                        .SetCode(TransportNotAllowedException.Code)
                        .Build());
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The root field names of every selected query operation (all operations when no name is given: fail-closed), following fragment
    /// spreads and inline fragments. Aliases do not matter, and a field behind <c>@skip</c>/<c>@include</c> still counts (conservative).
    /// </summary>
    internal static IEnumerable<string> RootFieldNames(DocumentNode document, string? operationName)
    {
        var fragments = new Dictionary<string, FragmentDefinitionNode>(StringComparer.Ordinal);
        foreach (var definition in document.Definitions)
        {
            if (definition is FragmentDefinitionNode fragment)
            {
                fragments[fragment.Name.Value] = fragment;
            }
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var definition in document.Definitions)
        {
            if (definition is OperationDefinitionNode { Operation: OperationType.Query } operation &&
                (string.IsNullOrEmpty(operationName) || string.Equals(operation.Name?.Value, operationName, StringComparison.Ordinal)))
            {
                Collect(operation.SelectionSet, fragments, visited, names);
            }
        }

        return names;
    }

    private static void Collect(SelectionSetNode selectionSet, Dictionary<string, FragmentDefinitionNode> fragments, HashSet<string> visited, List<string> names)
    {
        foreach (var selection in selectionSet.Selections)
        {
            switch (selection)
            {
                case FieldNode field:
                    names.Add(field.Name.Value);
                    break;
                case InlineFragmentNode inline:
                    Collect(inline.SelectionSet, fragments, visited, names);
                    break;
                case FragmentSpreadNode spread when fragments.TryGetValue(spread.Name.Value, out var fragment) && visited.Add(spread.Name.Value):
                    Collect(fragment.SelectionSet, fragments, visited, names);
                    break;
            }
        }
    }
}

/// <summary>Feature on a catalog root field that names its logical data source (read by <see cref="TransportGateMiddleware"/>).</summary>
public sealed record TransportSourceFeature(string LogicalSource);
