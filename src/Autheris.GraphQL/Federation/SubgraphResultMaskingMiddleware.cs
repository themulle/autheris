namespace Autheris.GraphQL.Federation;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Federation.Interfaces;
using Autheris.Application.Federation.Services;
using Autheris.Domain.Options;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

/// <summary>
/// Hot Chocolate request pipeline middleware that executes after query completion to enforce
/// Zero-Trust in-memory column and PII data masking on federated subgraph results.
/// </summary>
public sealed class SubgraphResultMaskingMiddleware
{
    private readonly RequestDelegate _next;

    public SubgraphResultMaskingMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        await _next(context).ConfigureAwait(false);

        var options = context.RequestServices.GetService<IOptions<GatewayOptions>>();
        if (options?.Value?.Federation?.Enabled != true ||
            !options.Value.Federation.EnableResultMasking ||
            options.Value.IsColumnMaskingDisabled)
        {
            return;
        }

        if (context.Result is OperationResult opResult && opResult.Data is { } originalData && originalData.Value != null)
        {
            var masker = context.RequestServices.GetService<ISubgraphResultMasker>();
            if (masker == null) return;

            HttpContext? httpContext = null;
            if (context.ContextData.TryGetValue("HttpContext", out var hcObj) && hcObj is HttpContext hc)
            {
                httpContext = hc;
            }
            else
            {
                httpContext = context.RequestServices.GetService<IHttpContextAccessor>()?.HttpContext;
            }

            var principal = httpContext?.User;
            var doc = context.OperationDocumentInfo?.Document;
            var aliasMap = ExtractAliasToFieldMap(doc);
            var maskedObj = masker.MaskResultData(originalData.Value, principal, aliasMap);

            if (maskedObj != null)
            {
                if (httpContext != null)
                {
                    httpContext.Items["MaskingApplied"] = true;
                }
                context.ContextData["MaskingApplied"] = true;

                var newData = new OperationResultData(maskedObj, isValueNull: false, originalData.Formatter, originalData.MemoryHolder);
                var newResult = new OperationResult(
                    newData,
                    opResult.Errors ?? System.Collections.Immutable.ImmutableList<HotChocolate.IError>.Empty,
                    opResult.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty);
                newResult.ContextData = opResult.ContextData;
                context.Result = newResult;
            }
        }
    }

    internal static Dictionary<string, string>? ExtractAliasToFieldMap(HotChocolate.Language.DocumentNode? document)
    {
        if (document == null) return null;

        // SEC (Niedrig): Benannte Fragmente (FragmentSpread) werden aufgelöst, damit Aliase darin ebenfalls erfasst werden.
        var fragments = new Dictionary<string, HotChocolate.Language.FragmentDefinitionNode>(StringComparer.Ordinal);
        foreach (var def in document.Definitions)
        {
            if (def is HotChocolate.Language.FragmentDefinitionNode fragment)
            {
                fragments.TryAdd(fragment.Name.Value, fragment);
            }
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in document.Definitions)
        {
            if (def is HotChocolate.Language.OperationDefinitionNode op)
            {
                TraverseSelections(op.SelectionSet, null, map, fragments, new HashSet<string>(StringComparer.Ordinal));
            }
        }

        return map;
    }

    private static void TraverseSelections(
        HotChocolate.Language.SelectionSetNode? selectionSet,
        string? parentPath,
        Dictionary<string, string> map,
        IReadOnlyDictionary<string, HotChocolate.Language.FragmentDefinitionNode> fragments,
        HashSet<string> activeFragments)
    {
        if (selectionSet == null) return;

        foreach (var sel in selectionSet.Selections)
        {
            if (sel is HotChocolate.Language.FieldNode field)
            {
                // SEC (Federation alias collision): key the map by response path (chain of response keys), not by the bare alias.
                var hasAlias = field.Alias != null && !string.IsNullOrWhiteSpace(field.Alias.Value);
                var responseKey = hasAlias ? field.Alias!.Value : field.Name.Value;
                var path = SubgraphResultMasker.AppendPath(parentPath, responseKey);

                if (hasAlias)
                {
                    // The same path can be produced by different type conditions (unions/interfaces); keep a sensitive target.
                    if (!map.TryGetValue(path, out var existing) || !SubgraphResultMasker.IsSensitiveField(existing))
                    {
                        map[path] = field.Name.Value;
                    }
                }

                TraverseSelections(field.SelectionSet, path, map, fragments, activeFragments);
            }
            else if (sel is HotChocolate.Language.InlineFragmentNode frag)
            {
                TraverseSelections(frag.SelectionSet, parentPath, map, fragments, activeFragments);
            }
            else if (sel is HotChocolate.Language.FragmentSpreadNode spread &&
                     fragments.TryGetValue(spread.Name.Value, out var fragmentDef) &&
                     activeFragments.Add(fragmentDef.Name.Value))
            {
                TraverseSelections(fragmentDef.SelectionSet, parentPath, map, fragments, activeFragments);
                activeFragments.Remove(fragmentDef.Name.Value);
            }
        }
    }
}
