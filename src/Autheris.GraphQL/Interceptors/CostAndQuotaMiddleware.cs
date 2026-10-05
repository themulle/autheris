namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Interfaces;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

public sealed class CostAndQuotaMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRateLimiterService _rateLimiter;

    public CostAndQuotaMiddleware(RequestDelegate next, IRateLimiterService rateLimiter)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
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
                // Ignore parse error, next pipeline stages will handle validation/syntax error
            }
        }

        if (httpContext == null || doc == null || context.Schema == null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var gatewayOptions = httpContext.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>>()?.Value;
        if (gatewayOptions?.IsRateLimitingDisabled == true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // 1. Resolve client tier & quota policy
        var tierResolver = httpContext.RequestServices.GetRequiredService<IClientTierResolver>();
        string? apiKey = httpContext.Request.Headers.TryGetValue("X-API-Key", out var ak) ? ak.ToString() : null;
        string? clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var principal = httpContext.User ?? (context.ContextData.TryGetValue("ClaimsPrincipal", out var cpObj) && cpObj is System.Security.Claims.ClaimsPrincipal cp ? cp : null);

        var clientContext = await tierResolver.ResolveAsync(principal, apiKey, clientIp, context.RequestAborted).ConfigureAwait(false);

        // 2. Calculate cost via QueryCostAnalyzerRule
        int calculatedCost = QueryCostAnalyzerRule.CalculateCost(doc, context.Schema);

        // 3. Check query cost against tier policy max limit
        if (calculatedCost > clientContext.Policy.MaxCostPerQuery)
        {
            var error = ErrorBuilder.New()
                .SetMessage($"Die Abfragekosten ({calculatedCost}) überschreiten das Tier-Limit von {clientContext.Policy.MaxCostPerQuery}.")
                .SetCode("QUERY_COST_QUOTA_EXCEEDED")
                .SetExtension("calculatedCost", calculatedCost)
                .SetExtension("maxAllowedCost", clientContext.Policy.MaxCostPerQuery)
                .Build();
            context.Result = OperationResult.FromError(error);
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // 4. Check token bucket rate limiter in Redis / in-memory
        var limitResult = await _rateLimiter.CheckCostQuotaAsync(
            clientContext.SubjectId,
            calculatedCost,
            clientContext.Policy,
            context.RequestAborted).ConfigureAwait(false);

        if (!limitResult.Allowed)
        {
            var error = ErrorBuilder.New()
                .SetMessage("Rate Limit überschritten. Bitte warten Sie bis zum nächsten Zeitfenster.")
                .SetCode("RATE_LIMIT_EXCEEDED")
                .SetExtension("retryAfterSeconds", limitResult.RetryAfterSeconds)
                .Build();
            context.Result = OperationResult.FromError(error);
            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            httpContext.Response.Headers.RetryAfter = limitResult.RetryAfterSeconds.ToString();
            return;
        }

        // 5. Execute query
        await _next(context).ConfigureAwait(false);

        // 6. Enrich response headers and extensions
        httpContext.Response.Headers["X-Query-Cost"] = calculatedCost.ToString();
        httpContext.Response.Headers["X-RateLimit-Remaining"] = limitResult.RemainingTokens.ToString();

        if (clientContext.Policy.ExposeCostExtensions && context.Result is OperationResult opResult)
        {
            var extensions = opResult.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
            opResult.Extensions = extensions.SetItem("cost", new Dictionary<string, object?>
            {
                ["requestedQueryCost"] = calculatedCost,
                ["clientTier"] = clientContext.Tier.ToString(),
                ["rateLimitRemaining"] = limitResult.RemainingTokens,
                ["rateLimitResetSeconds"] = limitResult.RetryAfterSeconds
            });
        }
    }
}
