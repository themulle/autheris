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

    private static Dictionary<string, object?>? BuildRuntimeVariables(RequestContext context)
    {
        if (context.VariableValues.IsDefaultOrEmpty)
        {
            return null;
        }

        var runtimeVariables = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var coll in context.VariableValues)
        {
            foreach (var item in coll)
            {
                runtimeVariables[item.Name] = item.Value;
            }
        }

        return runtimeVariables;
    }

    private static int EffectiveGatewayMaxCost(GatewayOptions gatewayOptions) =>
        gatewayOptions.AreQueryLimitsRelaxed
            ? (gatewayOptions.AllowInsecureWarnFlagsInProduction ? 100000 : 5000)
            : gatewayOptions.GraphQL.MaxAllowedComplexity;

    /// <summary>
    /// The gateway-wide complexity limit on the runtime variable values, for the paths that skip the tier and quota gate (rate limiting
    /// disabled, no HTTP context). Returns true when it rejected the request.
    /// </summary>
    private static bool RejectIfOverGatewayComplexity(RequestContext context, DocumentNode doc, GatewayOptions? gatewayOptions, HttpContext? httpContext)
    {
        if (gatewayOptions is null)
        {
            return false;
        }

        var maxCost = EffectiveGatewayMaxCost(gatewayOptions);
        if (maxCost <= 0)
        {
            return false;
        }

        int cost = QueryCostAnalyzerRule.CalculateCost(
            doc,
            context.Schema,
            maxResponseRows: gatewayOptions.GraphQL?.MaxResponseRows ?? 1000,
            variableValues: BuildRuntimeVariables(context),
            costOptions: gatewayOptions.QueryCost,
            maxExecutionDepth: QueryCostAnalyzerRule.ResolveMaxExecutionDepth(gatewayOptions.GraphQL));
        if (cost <= maxCost)
        {
            return false;
        }

        context.Result = OperationResult.FromError(
            ErrorBuilder.New()
                .SetMessage("The query exceeds the allowed complexity.")
                .SetCode("QUERY_COST_QUOTA_EXCEEDED")
                .Build());
        if (httpContext is not null)
        {
            HttpResponseGuard.SetStatus(httpContext, StatusCodes.Status400BadRequest);
        }

        return true;
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
            if (doc != null && context.Schema != null
                && RejectIfOverGatewayComplexity(context, doc, context.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>()?.Value, httpContext))
            {
                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        var services = httpContext.RequestServices ?? context.RequestServices;
        var gatewayOptions = services?.GetService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>()?.Value;
        if (gatewayOptions?.IsRateLimitingDisabled == true)
        {
            // SEC-RG-05: the validation pass prices a variable page size without its value, so the runtime pass must still bound it here.
            if (RejectIfOverGatewayComplexity(context, doc, gatewayOptions, httpContext))
            {
                return;
            }

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

        // SEC-GOV177-08: only a validated identity (authenticated principal) passes this gate. The X-API-Key header is
        // never validated here (unregistered keys fall back to the anonymous IP bucket in the tier resolver), so its mere
        // presence must not count as authenticated; a registered key only selects the quota tier.
        bool isAuthenticated = principal?.Identity?.IsAuthenticated == true;
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
        var runtimeVariables = BuildRuntimeVariables(context);

        int maxResponseRows = gatewayOptions?.GraphQL?.MaxResponseRows ?? 1000;
        int maxExecutionDepth = QueryCostAnalyzerRule.ResolveMaxExecutionDepth(gatewayOptions?.GraphQL);
        int calculatedCost = QueryCostAnalyzerRule.CalculateCost(
            doc,
            context.Schema,
            maxResponseRows: maxResponseRows,
            variableValues: runtimeVariables,
            costOptions: gatewayOptions?.QueryCost,
            aggregation: services?.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.AnalyticsOptions>>()?.Value?.Aggregation,
            maxExecutionDepth: maxExecutionDepth);

        int maxAllowedCost = clientContext.Policy.MaxCostPerQuery;
        if (gatewayOptions is not null && EffectiveGatewayMaxCost(gatewayOptions) is > 0 and var effectiveGatewayMaxCost)
        {
            maxAllowedCost = Math.Min(maxAllowedCost, effectiveGatewayMaxCost);
        }

        // 3. Check query cost against effective max limit (min of Tier and MaxAllowedComplexity)
        if (calculatedCost > maxAllowedCost)
        {
            var usageRecorder = services?.GetService<IConsumerUsageRecorder>();
            if (usageRecorder != null)
            {
                var tableId = ExtractTableFromDocument(doc);
                var tenantId = principal.GetTenantId();
                var userSid = principal.GetUserSid() ?? new Sid(principal?.Identity?.Name ?? "anonymous");
                usageRecorder.RecordUsage(tableId, tenantId, userSid, ConsumerOutcome.DenyCost, 0, false);
            }

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
            var usageRecorder = services?.GetService<IConsumerUsageRecorder>();
            if (usageRecorder != null)
            {
                var tableId = ExtractTableFromDocument(doc);
                var tenantId = principal.GetTenantId();
                var userSid = principal.GetUserSid() ?? new Sid(principal?.Identity?.Name ?? "anonymous");
                usageRecorder.RecordUsage(tableId, tenantId, userSid, ConsumerOutcome.DenyRateLimit, 0, false);
            }

            var errorBuilder = ErrorBuilder.New()
                .SetMessage("Rate limit exceeded. Wait until the next time window.")
                .SetCode("RATE_LIMIT_EXCEEDED")
                .SetExtension("retryAfterSeconds", limitResult.RetryAfterSeconds);

            if (!string.IsNullOrWhiteSpace(clientContext.ResolvedScope))
            {
                errorBuilder.SetExtension("scope", clientContext.ResolvedScope);
            }

            var error = errorBuilder.Build();
            context.Result = OperationResult.FromError(error);
            HttpResponseGuard.SetStatus(httpContext, StatusCodes.Status429TooManyRequests);
            HttpResponseGuard.SetHeader(httpContext, "Retry-After", limitResult.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(clientContext.ResolvedScope))
            {
                HttpResponseGuard.SetHeader(httpContext, "X-RateLimit-Scope", clientContext.ResolvedScope);
            }
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
                // SEC-GOV177-03: Mandatory minimum fee on client abort or execution failure.
                // SEC-GOV177-07: the failure fee is only a floor. A partial result (e.g. a successful 1000-row root field next
                // to a deliberately failing one) is charged for the data it produced, so an error cannot refund real work.
                actualCost = ComputeFailedCost(context, gatewayOptions?.QueryCost, clientContext.Policy);
            }
            else
            {
                actualCost = ComputeActualCost(context, gatewayOptions?.QueryCost, clientContext.Policy);
            }
        }
        catch
        {
            // SEC-GOV177-03: Mandatory minimum fee on exception (SEC-GOV177-07: rows already produced are still charged).
            actualCost = ComputeFailedCost(context, gatewayOptions?.QueryCost, clientContext.Policy);
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

    /// <summary>
    /// SEC-GOV177-03 / SEC-GOV177-07: settlement of a failed, aborted or partially failed request. The failure fee
    /// (at least BaseCost) is the floor; when data was produced, the actual cost of that data is charged if it is higher.
    /// </summary>
    internal static int ComputeFailedCost(
        RequestContext context,
        QueryCostOptions? costOptions,
        ClientQuotaPolicy policy)
    {
        int failureFee = Math.Max(costOptions?.BaseCost ?? 1, costOptions?.FailureFee ?? 1);
        if (!HasProducedData(context))
        {
            return failureFee;
        }

        int computed;
        try
        {
            computed = ComputeActualCost(context, costOptions, policy);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Counting must never turn a failed request into a free one; the caller still settles at least the failure fee.
            computed = failureFee;
        }

        return Math.Max(failureFee, computed);
    }

    /// <summary>True when the request produced result data (a data payload, or rows reported by the resolvers).</summary>
    internal static bool HasProducedData(RequestContext context)
    {
        if (context.Result is OperationResult opResult && opResult.Data is not null)
        {
            return true;
        }

        if (context.ContextData.TryGetValue("AutherisRowsReturned", out var rObj))
        {
            return rObj switch
            {
                long rLong => rLong > 0,
                int rInt => rInt > 0,
                _ => false
            };
        }

        return false;
    }

    internal static int ComputeActualCost(
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

        if (context.Result is OperationResult opResult && opResult.Data is not null)
        {
            var counted = CountResultRows(opResult.Data.Value);
            if (counted > 0)
            {
                rowsReturned = Math.Max(rowsReturned, counted);
            }
        }

        int rowCost = (int)Math.Ceiling(rowsReturned * rowFactor);
        int depthCost = executedDepth * depthFactor;
        long totalComputed = (long)baseCost + rowCost + depthCost;

        return (int)Math.Clamp(totalComputed, baseCost, policy.MaxCostPerQuery);
    }

    internal static long CountResultRows(object? data)
    {
        if (data is null) return 0;
        return CountRootRows(data, 0);
    }

    private static long CountRootRows(object data, int depth)
    {
        if (depth > 32) return 0;
        long total = 0;

        if (data is IReadOnlyDictionary<string, object?> dict)
        {
            foreach (var kvp in dict)
            {
                if (kvp.Value is null) continue;
                if (kvp.Value is string)
                {
                    total += 1;
                }
                else if (kvp.Value is System.Collections.IEnumerable enumerable)
                {
                    total += CountListRows(enumerable, depth + 1);
                }
                else if (kvp.Value is IReadOnlyDictionary<string, object?> childDict)
                {
                    total += 1 + CountNestedChildren(childDict, depth + 1);
                }
                else if (kvp.Value is System.Collections.IDictionary childLegacy)
                {
                    total += 1 + CountNestedChildren(childLegacy, depth + 1);
                }
                else
                {
                    total += 1;
                }
            }
            return total;
        }

        if (data is System.Collections.IDictionary legacyDict)
        {
            foreach (System.Collections.DictionaryEntry entry in legacyDict)
            {
                if (entry.Value is null) continue;
                if (entry.Value is string)
                {
                    total += 1;
                }
                else if (entry.Value is System.Collections.IEnumerable enumerable)
                {
                    total += CountListRows(enumerable, depth + 1);
                }
                else if (entry.Value is IReadOnlyDictionary<string, object?> childDict)
                {
                    total += 1 + CountNestedChildren(childDict, depth + 1);
                }
                else if (entry.Value is System.Collections.IDictionary childLegacy)
                {
                    total += 1 + CountNestedChildren(childLegacy, depth + 1);
                }
                else
                {
                    total += 1;
                }
            }
            return total;
        }

        if (data is System.Collections.IEnumerable topList and not string)
        {
            return CountListRows(topList, depth + 1);
        }

        return 1;
    }

    private static long CountListRows(System.Collections.IEnumerable enumerable, int depth)
    {
        if (depth > 32) return 0;
        long count = 0;
        foreach (var item in enumerable)
        {
            count += 1;
            if (item is IReadOnlyDictionary<string, object?> dict)
            {
                count += CountNestedChildren(dict, depth + 1);
            }
            else if (item is System.Collections.IDictionary legacy)
            {
                count += CountNestedChildren(legacy, depth + 1);
            }
            else if (item is System.Collections.IEnumerable nestedList and not string)
            {
                count += CountListRows(nestedList, depth + 1);
            }
        }
        return count;
    }

    private static long CountNestedChildren(IReadOnlyDictionary<string, object?> dict, int depth)
    {
        if (depth > 32) return 0;
        long count = 0;
        foreach (var kvp in dict)
        {
            if (kvp.Value is null || kvp.Value is string) continue;
            if (kvp.Value is System.Collections.IEnumerable enumerable)
            {
                count += CountListRows(enumerable, depth + 1);
            }
            else if (kvp.Value is IReadOnlyDictionary<string, object?> childDict)
            {
                count += 1 + CountNestedChildren(childDict, depth + 1);
            }
            else if (kvp.Value is System.Collections.IDictionary childLegacy)
            {
                count += 1 + CountNestedChildren(childLegacy, depth + 1);
            }
        }
        return count;
    }

    private static long CountNestedChildren(System.Collections.IDictionary dict, int depth)
    {
        if (depth > 32) return 0;
        long count = 0;
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            if (entry.Value is null || entry.Value is string) continue;
            if (entry.Value is System.Collections.IEnumerable enumerable)
            {
                count += CountListRows(enumerable, depth + 1);
            }
            else if (entry.Value is System.Collections.IDictionary childLegacy)
            {
                count += 1 + CountNestedChildren(childLegacy, depth + 1);
            }
            else if (entry.Value is IReadOnlyDictionary<string, object?> childDict)
            {
                count += 1 + CountNestedChildren(childDict, depth + 1);
            }
        }
        return count;
    }

    private static TableIdentifier ExtractTableFromDocument(DocumentNode? doc)
    {
        if (doc != null)
        {
            var op = doc.Definitions.OfType<OperationDefinitionNode>().FirstOrDefault();
            var field = op?.SelectionSet.Selections.OfType<FieldNode>().FirstOrDefault();
            if (field != null && !string.IsNullOrWhiteSpace(field.Name.Value))
            {
                var val = field.Name.Value;
                if (val.Contains('_'))
                {
                    var parts = val.Split('_', 2);
                    return new TableIdentifier(parts[0], "public", parts[1]);
                }
                return new TableIdentifier("graphql", "public", val);
            }
        }
        return new TableIdentifier("graphql", "query", "graphql");
    }
}
