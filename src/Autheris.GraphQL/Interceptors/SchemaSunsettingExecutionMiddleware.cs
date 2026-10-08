namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Model;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

public sealed class SchemaSunsettingExecutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ISchemaSunsettingService _sunsettingService;

    public SchemaSunsettingExecutionMiddleware(RequestDelegate next, ISchemaSunsettingService sunsettingService)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _sunsettingService = sunsettingService ?? throw new ArgumentNullException(nameof(sunsettingService));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        DocumentNode? doc = context.OperationDocumentInfo?.Document;
        if (doc == null && context.Request.Document is IOperationDocumentNodeProvider nodeProvider)
        {
            doc = nodeProvider.Document;
        }

        if (doc == null && context.Request.Document is not null)
        {
            try
            {
                var docStr = context.Request.Document.ToString();
                if (!string.IsNullOrWhiteSpace(docStr))
                {
                    doc = Utf8GraphQLParser.Parse(docStr);
                }
            }
            catch
            {
                // Ignore parse errors, let downstream pipeline handle them
            }
        }

        if (doc == null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        HttpContext? httpContext = null;
        if (context.ContextData.TryGetValue("HttpContext", out var hcObj) && hcObj is HttpContext hc)
        {
            httpContext = hc;
        }
        else
        {
            var accessor = context.RequestServices.GetService<IHttpContextAccessor>();
            httpContext = accessor?.HttpContext;
        }

        var referencedFields = ExtractReferencedFields(doc);
        var activeNotices = new List<SunsettingEvaluationResult>();

        foreach (var (table, field) in referencedFields)
        {
            var eval = await _sunsettingService.EvaluateFieldAsync(table, field, null, context.RequestAborted).ConfigureAwait(false);
            if (eval == null)
            {
                continue;
            }

            if (eval.IsHardSunsetBlocked)
            {
                var error = ErrorBuilder.New()
                    .SetMessage(eval.DeprecationNotice)
                    .SetCode("FIELD_PERMANENTLY_DECOMMISSIONED")
                    .SetExtension("field", field)
                    .SetExtension("table", table)
                    .SetExtension("sunsetting", eval.ExtensionsData)
                    .Build();

                context.Result = OperationResult.FromError(error);
                return;
            }

            if (eval.ShouldRejectWith426)
            {
                var error = ErrorBuilder.New()
                    .SetMessage(eval.DeprecationNotice)
                    .SetCode("UPGRADE_REQUIRED")
                    .SetExtension("field", field)
                    .SetExtension("table", table)
                    .SetExtension("httpStatusCode", 426)
                    .SetExtension("sunsetting", eval.ExtensionsData)
                    .Build();

                context.Result = OperationResult.FromError(error);
                return;
            }

            if (eval.ShouldInjectSyntheticLatency && eval.SyntheticLatencyMs > 0)
            {
                await Task.Delay(eval.SyntheticLatencyMs, context.RequestAborted).ConfigureAwait(false);
            }

            if (httpContext != null && !string.IsNullOrWhiteSpace(eval.HttpSunsetHeader))
            {
                HttpResponseGuard.SetHeader(httpContext, "Sunset", eval.HttpSunsetHeader);
                HttpResponseGuard.SetHeader(httpContext, "Deprecation", "@" + eval.SunsetDate.ToUnixTimeSeconds());
            }

            activeNotices.Add(eval);
        }

        await _next(context).ConfigureAwait(false);

        if (activeNotices.Count > 0 && context.Result is OperationResult opResult)
        {
            var sunsettingPayload = new Dictionary<string, object?>
            {
                ["notices"] = activeNotices.Select(n => n.ExtensionsData).ToList()
            };

            var extensions = opResult.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
            opResult.Extensions = extensions.SetItem("sunsetting", sunsettingPayload);
        }
    }

    private static HashSet<(string Table, string Field)> ExtractReferencedFields(DocumentNode document)
    {
        var fields = new HashSet<(string Table, string Field)>();

        foreach (var definition in document.Definitions)
        {
            if (definition is OperationDefinitionNode opDef)
            {
                TraverseSelectionSet(opDef.SelectionSet, parentType: string.Empty, fields);
            }
        }

        return fields;
    }

    private static void TraverseSelectionSet(
        SelectionSetNode selectionSet,
        string parentType,
        HashSet<(string Table, string Field)> fields)
    {
        foreach (var selection in selectionSet.Selections)
        {
            if (selection is FieldNode fieldNode)
            {
                var fieldName = fieldNode.Name.Value;
                if (!fieldName.StartsWith("__", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(parentType))
                    {
                        fields.Add((parentType, fieldName));
                    }

                    if (fieldNode.SelectionSet != null)
                    {
                        TraverseSelectionSet(fieldNode.SelectionSet, parentType: fieldName, fields);
                    }
                }
            }
            else if (selection is InlineFragmentNode inlineFrag)
            {
                var targetType = inlineFrag.TypeCondition?.Name.Value ?? parentType;
                TraverseSelectionSet(inlineFrag.SelectionSet, targetType, fields);
            }
        }
    }
}
