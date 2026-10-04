namespace Autheris.GraphQL.Interceptors;

using System;
using System.Linq;
using System.Threading.Tasks;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

public sealed class CdnCacheTagMiddleware
{
    private readonly RequestDelegate _next;

    public CdnCacheTagMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        await _next(context).ConfigureAwait(false);

        HttpContext? httpContext = null;
        if (context.ContextData.TryGetValue("HttpContext", out var hcObj) && hcObj is HttpContext hc)
        {
            httpContext = hc;
        }

        var doc = context.OperationDocumentInfo?.Document;

        if (httpContext == null || doc == null)
        {
            return;
        }

        // Only cache queries, never mutations or subscriptions
        var isQueryOnly = doc.Definitions
            .OfType<OperationDefinitionNode>()
            .All(op => op.Operation == OperationType.Query);

        if (!isQueryOnly)
        {
            SetPrivateNoStore(httpContext);
            return;
        }

        // SEC (Niedrig): Fehlerantworten und Nicht-Standard-Ergebnisse (Streams/Batches) niemals public cachen.
        if (context.Result is not OperationResult opResult || (opResult.Errors is not null && opResult.Errors.Count > 0))
        {
            SetPrivateNoStore(httpContext);
            return;
        }

        // Zero-Trust Gate: Check if user-specific RLS or PII masking was applied
        bool isPersonalOrRestricted =
            httpContext.Items.ContainsKey("RlsApplied") ||
            httpContext.Items.ContainsKey("MaskingApplied") ||
            (context.ContextData.TryGetValue("RlsApplied", out var rls) && rls is true) ||
            (context.ContextData.TryGetValue("MaskingApplied", out var mask) && mask is true) ||
            httpContext.User?.Identity?.IsAuthenticated == true;

        if (isPersonalOrRestricted)
        {
            SetPrivateNoStore(httpContext);
            return;
        }

        // Public unconditioned data: generate Cache-Tag and Surrogate-Key headers
        var tagDescriptor = CdnCacheTagVisitor.ExtractTags(doc);
        var allTags = tagDescriptor.TypeTags.Concat(tagDescriptor.EntityTags).Distinct().ToList();

        if (allTags.Count > 0)
        {
            httpContext.Response.Headers["Cache-Control"] = "public, s-maxage=300, stale-while-revalidate=60";
            httpContext.Response.Headers["Cache-Tag"] = string.Join(", ", allTags);
            httpContext.Response.Headers["Surrogate-Key"] = string.Join(" ", allTags);
            // SEC (Niedrig): Katalog-Ergebnisse hängen vom X-Domain-Scope-Header ab.
            httpContext.Response.Headers["Vary"] = "Accept-Encoding, Origin, X-Domain-Scope";
        }
    }

    private static void SetPrivateNoStore(HttpContext httpContext)
    {
        httpContext.Response.Headers["Cache-Control"] = "private, no-cache, no-store, must-revalidate";
        httpContext.Response.Headers["Pragma"] = "no-cache";
        httpContext.Response.Headers.Remove("Cache-Tag");
        httpContext.Response.Headers.Remove("Surrogate-Key");
    }
}
