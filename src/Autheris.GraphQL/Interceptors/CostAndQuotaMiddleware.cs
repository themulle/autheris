namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;
using RequestContext = HotChocolate.Execution.RequestContext;

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

        if (httpContext != null && httpContext.RequestServices == null)
        {
            httpContext.RequestServices = context.RequestServices;
        }

        if (httpContext == null || doc == null || context.Schema == null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var services = httpContext.RequestServices ?? context.RequestServices;
        var gatewayOptions = services?.GetService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>()?.Value;
        if (gatewayOptions?.IsRateLimitingDisabled == true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // SEC-GOV177-06: Zero Unauthenticated Allocation
        // Unauthenticated requests, requests with invalid tokens, or rejected requests MUST NEVER touch rate limiter buckets.
        var principal = httpContext.User ?? (context.ContextData.TryGetValue("ClaimsPrincipal", out var cpObj) && cpObj is System.Security.Claims.ClaimsPrincipal cp ? cp : null);
        string? apiKey = httpContext.Request.Headers.TryGetValue("X-API-Key", out var ak) ? ak.ToString() : null;
        string? clientIp = httpContext.Connection.RemoteIpAddress?.ToString();

        if (httpContext.Response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            return;
        }

        bool isAuthenticated = principal?.Identity?.IsAuthenticated == true || !string.IsNullOrWhiteSpace(apiKey);
        if (!isAuthenticated && gatewayOptions?.IsAnonymousAccessAllowed != true)
        {
            HttpResponseGuard.SetStatus(httpContext, StatusCodes.Status401Unauthorized);
            context.Result = OperationResult.FromError(
                ErrorBuilder.New().SetMessage("Unauthorized.").SetCode("AUTH_NOT_AUTHENTICATED").Build());
            return;
        }

        // 1. Resolve client tier & quota policy
        var tierResolver = services!.GetRequiredService<IClientTierResolver>();
        var clientContext = await tierResolver.ResolveAsync(principal, apiKey, clientIp, context.RequestAborted).ConfigureAwait(false);

        // 2. Calculate cost via QueryCostAnalyzerRule (incorporating actual runtime variable values and QueryCostOptions)
        Dictionary<string, object?>? runtimeVariables = null;
        if (!context.VariableValues.IsDefaultOrEmpty)
        {
            runtimeVariables = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var coll in context.VariableValues)
            {
                foreach (var item in coll)
                {
                    runtimeVariables[item.Name] = item.Value;
                }
            }
        }

        int maxResponseRows = gatewayOptions?.GraphQL?.MaxResponseRows ?? 1000;
        int calculatedCost = QueryCostAnalyzerRule.CalculateCost(
            doc,
            context.Schema,
            maxResponseRows: maxResponseRows,
            variableValues: runtimeVariables,
            costOptions: gatewayOptions?.QueryCost,
            aggregation: services?.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.AnalyticsOptions>>()?.Value?.Aggregation);

        int maxAllowedCost = clientContext.Policy.MaxCostPerQuery;
        if (gatewayOptions is not null)
        {
            var effectiveGatewayMaxCost = gatewayOptions.AreQueryLimitsRelaxed
                ? (gatewayOptions.AllowInsecureWarnFlagsInProduction ? 100000 : 5000)
                : gatewayOptions.GraphQL.MaxAllowedComplexity;
            if (effectiveGatewayMaxCost > 0)
            {
                maxAllowedCost = Math.Min(maxAllowedCost, effectiveGatewayMaxCost);
            }
        }

        // 3. Check query cost against effective max limit (min of Tier and MaxAllowedComplexity)
        if (calculatedCost > maxAllowedCost)
        {
            var error = ErrorBuilder.New()
                .SetMessage($"The query cost ({calculatedCost}) exceeds the limit of {maxAllowedCost}. Nested list fields require 'first' (e.g. first: 10) or lower pagination limits to bound query cost within budget.")
                .SetCode("QUERY_COST_QUOTA_EXCEEDED")
                .SetExtension("calculatedCost", calculatedCost)
                .SetExtension("maxAllowedCost", maxAllowedCost)
                .SetExtension("hint", "Add 'first: <n>' to nested list selections or declare lower pagination limits to reduce query cost within budget.")
                .Build();
            context.Result = OperationResult.FromError(error);
            HttpResponseGuard.SetStatus(httpContext, StatusCodes.Status400BadRequest);
            return;
        }

        // 4. SEC-GOV177-01: Phase 1 Pre-Admission Reservation via ReserveCostQuotaAsync
        var limitResult = await _rateLimiter.ReserveCostQuotaAsync(
            clientContext.SubjectId,
            calculatedCost,
            clientContext.Policy,
            context.RequestAborted).ConfigureAwait(false);

        if (!limitResult.Allowed)
        {
            var error = ErrorBuilder.New()
                .SetMessage("Rate limit exceeded. Wait until the next time window.")
                .SetCode("RATE_LIMIT_EXCEEDED")
                .SetExtension("retryAfterSeconds", limitResult.RetryAfterSeconds)
                .Build();
            context.Result = OperationResult.FromError(error);
            HttpResponseGuard.SetStatus(httpContext, StatusCodes.Status429TooManyRequests);
            HttpResponseGuard.SetHeader(httpContext, "Retry-After", limitResult.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
            return;
        }

        // 5. Phase 2: Execute query with post-execution settlement in finally block
        int actualCost = calculatedCost;
        try
        {
            await _next(context).ConfigureAwait(false);
            if (context.RequestAborted.IsCancellationRequested ||
                context.Result is not OperationResult op ||
                (op.Errors is not null && op.Errors.Count > 0))
            {
                // SEC-GOV177-03: Mandatory minimum fee on client abort or execution failure
                actualCost = Math.Max(gatewayOptions?.QueryCost?.BaseCost ?? 1, gatewayOptions?.QueryCost?.FailureFee ?? 1);
            }
            else
            {
                actualCost = ComputeActualCost(context, gatewayOptions?.QueryCost, clientContext.Policy);
            }
        }
        catch
        {
            // SEC-GOV177-03: Mandatory minimum fee on exception
            actualCost = Math.Max(gatewayOptions?.QueryCost?.BaseCost ?? 1, gatewayOptions?.QueryCost?.FailureFee ?? 1);
            throw;
        }
        finally
        {
            if (limitResult.Allowed && gatewayOptions?.QueryCost?.EnablePostExecutionSettlement != false)
            {
                var settleResult = await _rateLimiter.SettleCostQuotaAsync(
                    clientContext.SubjectId,
                    calculatedCost,
                    actualCost,
                    clientContext.Policy,
                    CancellationToken.None).ConfigureAwait(false);

                // Update headers with settled figures
                HttpResponseGuard.SetHeader(httpContext, "X-Query-Cost", actualCost.ToString(CultureInfo.InvariantCulture));
                HttpResponseGuard.SetHeader(httpContext, "X-Query-Cost-Reserved", calculatedCost.ToString(CultureInfo.InvariantCulture));
                HttpResponseGuard.SetHeader(httpContext, "X-Query-Cost-Actual", actualCost.ToString(CultureInfo.InvariantCulture));
                HttpResponseGuard.SetHeader(httpContext, "X-Query-Cost-Refund", Math.Max(0, calculatedCost - actualCost).ToString(CultureInfo.InvariantCulture));
                HttpResponseGuard.SetHeader(httpContext, "X-RateLimit-Remaining", settleResult.RemainingTokens.ToString(CultureInfo.InvariantCulture));

                if (clientContext.Policy.ExposeCostExtensions && context.Result is OperationResult opResult)
                {
                    var extensions = opResult.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
                    opResult.Extensions = extensions.SetItem("cost", new Dictionary<string, object?>
                    {
                        ["requestedQueryCost"] = calculatedCost,
                        ["actualQueryCost"] = actualCost,
                        ["settlementDelta"] = calculatedCost - actualCost,
                        ["clientTier"] = clientContext.Tier.ToString(),
                        ["rateLimitRemaining"] = settleResult.RemainingTokens,
                        ["rateLimitResetSeconds"] = settleResult.RetryAfterSeconds
                    });
                }
            }
            else
            {
                HttpResponseGuard.SetHeader(httpContext, "X-Query-Cost", calculatedCost.ToString(CultureInfo.InvariantCulture));
                HttpResponseGuard.SetHeader(httpContext, "X-RateLimit-Remaining", limitResult.RemainingTokens.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static int ComputeActualCost(
        RequestContext context,
        QueryCostOptions? costOptions,
        ClientQuotaPolicy policy)
    {
        var options = costOptions ?? new QueryCostOptions();
        int baseCost = Math.Max(1, options.BaseCost);
        double rowFactor = Math.Max(0.0, options.RowCostFactor);
        int depthFactor = Math.Max(0, options.NestedCostFactor);

        long rowsReturned = 0;
        int executedDepth = 1;

        if (context.ContextData.TryGetValue("AutherisRowsReturned", out var rObj))
        {
            if (rObj is long rLong) rowsReturned = rLong;
            else if (rObj is int rInt) rowsReturned = rInt;
        }

        if (context.ContextData.TryGetValue("AutherisMaxExecutedDepth", out var dObj) && dObj is int dInt)
        {
            executedDepth = Math.Max(1, dInt);
        }

        if (rowsReturned == 0 && context.Result is OperationResult opResult && opResult.Data is not null)
        {
            rowsReturned = CountResultRows(opResult.Data.Value);
        }

        int rowCost = (int)Math.Ceiling(rowsReturned * rowFactor);
        int depthCost = executedDepth * depthFactor;
        long totalComputed = (long)baseCost + rowCost + depthCost;

        return (int)Math.Clamp(totalComputed, baseCost, policy.MaxCostPerQuery);
    }

    private static long CountResultRows(object? data)
    {
        if (data is null) return 0;
        long total = 0;
        if (data is IReadOnlyDictionary<string, object?> dict)
        {
            foreach (var kvp in dict)
            {
                if (kvp.Value is System.Collections.IEnumerable enumerable and not string)
                {
                    int count = 0;
                    foreach (var _ in enumerable) count++;
                    total += count;
                }
                else if (kvp.Value is not null)
                {
                    total += 1;
                }
            }
        }
        else if (data is System.Collections.IDictionary legacyDict)
        {
            foreach (System.Collections.DictionaryEntry entry in legacyDict)
            {
                if (entry.Value is System.Collections.IEnumerable enumerable and not string)
                {
                    int count = 0;
                    foreach (var _ in enumerable) count++;
                    total += count;
                }
                else if (entry.Value is not null)
                {
                    total += 1;
                }
            }
        }
        else if (data is System.Collections.IEnumerable enumerable and not string)
        {
            int count = 0;
            foreach (var _ in enumerable) count++;
            total += count;
        }
        else
        {
            total = 1;
        }
        return total;
    }
}
