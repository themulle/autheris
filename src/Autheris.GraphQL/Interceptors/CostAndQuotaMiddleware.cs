namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Common.Quota;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.State.Caching.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;
using RequestContext = HotChocolate.Execution.RequestContext;

public sealed class CostAndQuotaMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IQueryCostQuota _costQuota;

    public CostAndQuotaMiddleware(RequestDelegate next, IQueryCostQuota costQuota)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _costQuota = costQuota ?? throw new ArgumentNullException(nameof(costQuota));
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

    public const int MaxComplexitySafetyCeiling = 10000;

    public static int ResolveMaxAdmissionCeiling(GatewayOptions gatewayOptions)
    {
        if (gatewayOptions.AreQueryLimitsRelaxed)
        {
            return gatewayOptions.AllowInsecureWarnFlagsInProduction ? 100000 : 5000;
        }

        int max = gatewayOptions.GraphQL.MaxAllowedComplexity;
        if (gatewayOptions.RateLimiting?.ClientTiers?.TierLimits != null)
        {
            foreach (var tier in gatewayOptions.RateLimiting.ClientTiers.TierLimits.Values)
            {
                if (tier.MaxCostPerQuery.HasValue && tier.MaxCostPerQuery.Value > max)
                {
                    max = tier.MaxCostPerQuery.Value;
                }
            }
        }
        if (gatewayOptions.RateLimiting?.ClientTiers?.ScopeLimits != null)
        {
            foreach (var scope in gatewayOptions.RateLimiting.ClientTiers.ScopeLimits.Values)
            {
                if (scope.MaxCostPerQuery.HasValue && scope.MaxCostPerQuery.Value > max)
                {
                    max = scope.MaxCostPerQuery.Value;
                }
            }
        }

        return Math.Min(max, MaxComplexitySafetyCeiling);
    }

    internal static int EffectiveGatewayMaxCost(GatewayOptions gatewayOptions) =>
        ResolveMaxAdmissionCeiling(gatewayOptions);

    internal static void AttachIndexHintsToResult(
        OperationResult opResult,
        IReadOnlyList<GraphQlIndexHint>? indexHints)
    {
        if (indexHints is null || indexHints.Count == 0)
        {
            return;
        }

        if (opResult.Errors is not null && opResult.Errors.Count > 0)
        {
            return;
        }

        var extensions = opResult.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
        Dictionary<string, object?> autherisExt;
        if (extensions.TryGetValue("autheris", out var existing) &&
            existing is IReadOnlyDictionary<string, object?> roDict)
        {
            autherisExt = new Dictionary<string, object?>(roDict.Count + 1, StringComparer.Ordinal);
            foreach (var kvp in roDict)
            {
                autherisExt[kvp.Key] = kvp.Value;
            }
        }
        else if (existing is IDictionary<string, object?> dict)
        {
            autherisExt = new Dictionary<string, object?>(dict.Count + 1, StringComparer.Ordinal);
            foreach (var kvp in dict)
            {
                autherisExt[kvp.Key] = kvp.Value;
            }
        }
        else
        {
            autherisExt = new Dictionary<string, object?>(1, StringComparer.Ordinal);
        }

        autherisExt["indexHints"] = indexHints;
        opResult.Extensions = extensions.SetItem("autheris", autherisExt);
    }

    private static readonly IReadOnlySet<string> EmptyVisibleColumns =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static async ValueTask<Func<string, IReadOnlySet<string>?>?> ResolveVisibleColumnsLookupAsync(
        IServiceProvider? services,
        ClaimsPrincipal? principal,
        DocumentNode doc,
        Func<string, TableMetadata?>? metadataLookup,
        CancellationToken cancellationToken)
    {
        if (services is null || metadataLookup is null)
        {
            return null;
        }

        var pdp = services.GetService<IAccessDecisionPoint>();
        if (pdp is null)
        {
            return null;
        }

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return _ => EmptyVisibleColumns;
        }

        var rootFieldNames = CollectRootFieldNames(doc);
        if (rootFieldNames.Count == 0)
        {
            return _ => EmptyVisibleColumns;
        }

        var resolvedByQualifiedName = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        var byTableKey = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rootName in rootFieldNames)
        {
            var meta = metadataLookup(rootName);
            string? baseName = null;
            if (meta is null)
            {
                if (rootName.EndsWith("_aggregate", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = rootName[..^10];
                    meta = metadataLookup(baseName);
                }
                else if (rootName.EndsWith("Connection", StringComparison.Ordinal))
                {
                    baseName = rootName[..^10];
                    meta = metadataLookup(baseName);
                }
            }

            if (meta is null)
            {
                continue;
            }

            var qualifiedName = meta.Identifier.ToQualifiedName();
            if (!resolvedByQualifiedName.TryGetValue(qualifiedName, out var visibleCols))
            {
                try
                {
                    var decision = await GraphQlDataSecurityMiddleware
                        .DecideAccessAsync(pdp, principal, meta, requestedFields: null, cancellationToken)
                        .ConfigureAwait(false);
                    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (decision.IsAllowed)
                    {
                        foreach (var col in meta.Columns)
                        {
                            if (decision.GetEffectiveColumnAccess(col.ColumnName, meta) != ColumnAccessLevel.Deny)
                            {
                                allowed.Add(col.ColumnName);
                            }
                        }
                    }
                    visibleCols = allowed;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    visibleCols = EmptyVisibleColumns;
                }

                resolvedByQualifiedName[qualifiedName] = visibleCols;
            }

            byTableKey[rootName] = visibleCols;
            if (baseName is not null)
            {
                byTableKey[baseName] = visibleCols;
            }
            byTableKey[meta.Table.TableName] = visibleCols;
            byTableKey[qualifiedName] = visibleCols;
        }

        return key => byTableKey.TryGetValue(key, out var cols) ? cols : EmptyVisibleColumns;
    }

    private static List<string> CollectRootFieldNames(DocumentNode doc)
    {
        var names = new List<string>();
        var fragments = new Dictionary<string, FragmentDefinitionNode>(StringComparer.Ordinal);
        foreach (var def in doc.Definitions)
        {
            if (def is FragmentDefinitionNode frag)
            {
                fragments[frag.Name.Value] = frag;
            }
        }

        var visitedFragments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in doc.Definitions.OfType<OperationDefinitionNode>())
        {
            if (op.SelectionSet is not null)
            {
                CollectSelectionFieldNames(op.SelectionSet, fragments, visitedFragments, names);
            }
        }

        return names;
    }

    private static void CollectSelectionFieldNames(
        SelectionSetNode selectionSet,
        IReadOnlyDictionary<string, FragmentDefinitionNode> fragments,
        HashSet<string> visitedFragments,
        List<string> names)
    {
        foreach (var sel in selectionSet.Selections)
        {
            switch (sel)
            {
                case FieldNode field:
                    names.Add(field.Name.Value);
                    break;
                case InlineFragmentNode inlineFrag when inlineFrag.SelectionSet is not null:
                    CollectSelectionFieldNames(inlineFrag.SelectionSet, fragments, visitedFragments, names);
                    break;
                case FragmentSpreadNode spread when visitedFragments.Add(spread.Name.Value) &&
                    fragments.TryGetValue(spread.Name.Value, out var frag):
                    CollectSelectionFieldNames(frag.SelectionSet, fragments, visitedFragments, names);
                    break;
            }
        }
    }

    /// <summary>
    /// The gateway-wide complexity limit on the runtime variable values, for the paths that skip the tier and quota gate (rate limiting
    /// disabled, no HTTP context). Returns true when it rejected the request.
    /// </summary>
    private static async ValueTask<(bool Rejected, List<GraphQlIndexHint>? IndexHints)> RejectIfOverGatewayComplexityAsync(
        RequestContext context,
        DocumentNode doc,
        GatewayOptions? gatewayOptions,
        HttpContext? httpContext)
    {
        if (gatewayOptions is null)
        {
            return (false, null);
        }

        var maxCost = EffectiveGatewayMaxCost(gatewayOptions);
        var services = httpContext?.RequestServices ?? context.RequestServices;
        var tableMetadataLookup = CatalogGraphQlTypeModule.CurrentLookup;
        var visibleColumnsLookup = await ResolveVisibleColumnsLookupAsync(
                services,
                httpContext?.User,
                doc,
                tableMetadataLookup,
                context.RequestAborted)
            .ConfigureAwait(false);
        var relationRegistry = services.GetService<Autheris.GraphQL.Catalog.ICatalogRelationRegistry>();
        var analyticsOptions = services.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.AnalyticsOptions>>()?.Value;
        var indexHints = new List<GraphQlIndexHint>();
        int cost = QueryCostAnalyzerRule.CalculateCost(
            doc,
            context.Schema,
            maxResponseRows: gatewayOptions.GraphQL?.MaxResponseRows ?? 1000,
            variableValues: BuildRuntimeVariables(context),
            costOptions: gatewayOptions.QueryCost,
            aggregation: analyticsOptions?.Aggregation,
            maxExecutionDepth: QueryCostAnalyzerRule.ResolveMaxExecutionDepth(gatewayOptions.GraphQL),
            relationRegistry: relationRegistry,
            tableMetadataLookup: tableMetadataLookup,
            visibleColumnsLookup: visibleColumnsLookup,
            indexHintsOut: indexHints);
        if (maxCost > 0 && cost > maxCost)
        {
            context.Result = OperationResult.FromError(
                ErrorBuilder.New()
                    .SetMessage("The query exceeds the allowed complexity.")
                    .SetCode("QUERY_COST_QUOTA_EXCEEDED")
                    .Build());
            if (httpContext is not null)
            {
                HttpResponseGuard.SetStatus(httpContext, StatusCodes.Status400BadRequest);
            }

            return (true, null);
        }

        return (false, indexHints);
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
            List<GraphQlIndexHint>? unthrottledHints = null;
            if (doc != null && context.Schema != null)
            {
                var (rejected, hints) = await RejectIfOverGatewayComplexityAsync(
                        context,
                        doc,
                        context.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>()?.Value,
                        httpContext)
                    .ConfigureAwait(false);
                if (rejected)
                {
                    return;
                }

                unthrottledHints = hints;
            }

            await _next(context).ConfigureAwait(false);
            if (!context.RequestAborted.IsCancellationRequested && context.Result is OperationResult unthrottledOp)
            {
                AttachIndexHintsToResult(unthrottledOp, unthrottledHints);
            }
            return;
        }

        var services = httpContext.RequestServices ?? context.RequestServices;
        var gatewayOptions = services?.GetService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>()?.Value;
        if (gatewayOptions?.IsRateLimitingDisabled == true)
        {
            // the validation pass prices a variable page size without its value, so the runtime pass must still bound it here.
            var (rejected, hints) = await RejectIfOverGatewayComplexityAsync(context, doc, gatewayOptions, httpContext)
                .ConfigureAwait(false);
            if (rejected)
            {
                return;
            }

            await _next(context).ConfigureAwait(false);
            if (!context.RequestAborted.IsCancellationRequested && context.Result is OperationResult disabledOp)
            {
                AttachIndexHintsToResult(disabledOp, hints);
            }
            return;
        }

        // Zero Unauthenticated Allocation
        // Unauthenticated requests, requests with invalid tokens, or rejected requests MUST NEVER touch rate limiter buckets.
        var principal = httpContext.User ?? (context.ContextData.TryGetValue("ClaimsPrincipal", out var cpObj) && cpObj is System.Security.Claims.ClaimsPrincipal cp ? cp : null);
        string? apiKey = httpContext.Request.Headers.TryGetValue("X-API-Key", out var ak) ? ak.ToString() : null;
        string? clientIp = httpContext.Connection.RemoteIpAddress?.ToString();

        if (httpContext.Response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            return;
        }

        // only a validated identity (authenticated principal) passes this gate. The X-API-Key header is
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

        // 1. Calculate cost via QueryCostAnalyzerRule (incorporating actual runtime variable values and QueryCostOptions)
        var runtimeVariables = BuildRuntimeVariables(context);

        int maxResponseRows = gatewayOptions?.GraphQL?.MaxResponseRows ?? 1000;
        int maxExecutionDepth = QueryCostAnalyzerRule.ResolveMaxExecutionDepth(gatewayOptions?.GraphQL);
        var relationRegistry = services?.GetService<Autheris.GraphQL.Catalog.ICatalogRelationRegistry>();
        var tableMetadataLookup = CatalogGraphQlTypeModule.CurrentLookup;
        var visibleColumnsLookup = await ResolveVisibleColumnsLookupAsync(
                services,
                httpContext.User,
                doc,
                tableMetadataLookup,
                context.RequestAborted)
            .ConfigureAwait(false);
        var indexHints = new List<GraphQlIndexHint>();
        int calculatedCost = QueryCostAnalyzerRule.CalculateCost(
            doc,
            context.Schema,
            maxResponseRows: maxResponseRows,
            variableValues: runtimeVariables,
            costOptions: gatewayOptions?.QueryCost,
            aggregation: services?.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.AnalyticsOptions>>()?.Value?.Aggregation,
            maxExecutionDepth: maxExecutionDepth,
            relationRegistry: relationRegistry,
            tableMetadataLookup: tableMetadataLookup,
            visibleColumnsLookup: visibleColumnsLookup,
            indexHintsOut: indexHints);

        int maxAdmissionCeiling = gatewayOptions is not null ? EffectiveGatewayMaxCost(gatewayOptions) : MaxComplexitySafetyCeiling;
        if (maxAdmissionCeiling <= 0)
        {
            maxAdmissionCeiling = MaxComplexitySafetyCeiling;
        }

        var admission = await _costQuota.AdmitAsync(new QueryCostRequest(
            Principal: principal,
            ApiKey: apiKey,
            ClientIp: clientIp,
            EstimatedCost: calculatedCost,
            TransportMaxCost: maxAdmissionCeiling,
            CapByTierMaxCostPerQuery: true), context.RequestAborted).ConfigureAwait(false);

        context.ContextData["Autheris.ExposeCostExtensions"] = admission.Policy.ExposeCostExtensions;

        if (admission.Outcome == QueryCostOutcome.OverCost)
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
                .SetMessage($"The query cost ({calculatedCost}) exceeds the limit of {admission.MaxAllowedCost}. Nested list fields require 'first' (e.g. first: 10) or lower pagination limits to bound query cost within budget.")
                .SetCode("QUERY_COST_QUOTA_EXCEEDED")
                .SetExtension("calculatedCost", calculatedCost)
                .SetExtension("maxAllowedCost", admission.MaxAllowedCost)
                .SetExtension("hint", "Add 'first: <n>' to nested list selections or declare lower pagination limits to reduce query cost within budget.")
                .Build();
            context.Result = OperationResult.FromError(error);
            HttpResponseGuard.SetStatus(httpContext, StatusCodes.Status400BadRequest);
            return;
        }

        if (admission.Outcome == QueryCostOutcome.QuotaExhausted)
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
                .SetExtension("retryAfterSeconds", admission.RetryAfterSeconds);

            if (!string.IsNullOrWhiteSpace(admission.Scope))
            {
                errorBuilder.SetExtension("scope", admission.Scope);
            }

            var error = errorBuilder.Build();
            context.Result = OperationResult.FromError(error);
            HttpResponseGuard.SetStatus(httpContext, StatusCodes.Status429TooManyRequests);
            HttpResponseGuard.SetHeader(httpContext, "Retry-After", admission.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(admission.Scope))
            {
                HttpResponseGuard.SetHeader(httpContext, "X-RateLimit-Scope", admission.Scope);
            }
            return;
        }

        // Phase 2: Execute query with post-execution settlement in finally block
        int actualCost = calculatedCost;
        try
        {
            await _next(context).ConfigureAwait(false);
            if (context.RequestAborted.IsCancellationRequested ||
                context.Result is not OperationResult op ||
                (op.Errors is not null && op.Errors.Count > 0))
            {
                // Mandatory minimum fee on client abort or execution failure.
                // the failure fee is only a floor. A partial result (e.g. a successful 1000-row root field next
                // to a deliberately failing one) is charged for the data it produced, so an error cannot refund real work.
                actualCost = ComputeFailedCost(context, gatewayOptions?.QueryCost, admission.Policy);
            }
            else
            {
                actualCost = ComputeActualCost(context, gatewayOptions?.QueryCost, admission.Policy);
                AttachIndexHintsToResult(op, indexHints);
            }
        }
        catch
        {
            // Mandatory minimum fee on exception (rows already produced are still charged).
            actualCost = ComputeFailedCost(context, gatewayOptions?.QueryCost, admission.Policy);
            throw;
        }
        finally
        {
            if (admission.Outcome == QueryCostOutcome.Admitted && gatewayOptions?.QueryCost?.EnablePostExecutionSettlement != false)
            {
                var settleResult = await _costQuota.SettleAsync(
                    admission,
                    actualCost,
                    CancellationToken.None).ConfigureAwait(false);

                if (settleResult is { } settle)
                {
                    // Update headers with settled figures
                    HttpResponseGuard.SetHeader(httpContext, "X-Query-Cost", actualCost.ToString(CultureInfo.InvariantCulture));
                    HttpResponseGuard.SetHeader(httpContext, "X-Query-Cost-Reserved", calculatedCost.ToString(CultureInfo.InvariantCulture));
                    HttpResponseGuard.SetHeader(httpContext, "X-Query-Cost-Actual", actualCost.ToString(CultureInfo.InvariantCulture));
                    HttpResponseGuard.SetHeader(httpContext, "X-Query-Cost-Refund", Math.Max(0, calculatedCost - actualCost).ToString(CultureInfo.InvariantCulture));
                    HttpResponseGuard.SetHeader(httpContext, "X-RateLimit-Remaining", settle.RemainingTokens.ToString(CultureInfo.InvariantCulture));

                    if (admission.Policy.ExposeCostExtensions && context.Result is OperationResult opResult)
                    {
                        var extensions = opResult.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
                        opResult.Extensions = extensions.SetItem("cost", new Dictionary<string, object?>
                        {
                            ["requestedQueryCost"] = calculatedCost,
                            ["actualQueryCost"] = actualCost,
                            ["settlementDelta"] = calculatedCost - actualCost,
                            ["clientTier"] = admission.Policy.Tier.ToString(),
                            ["rateLimitRemaining"] = settle.RemainingTokens,
                            ["rateLimitResetSeconds"] = settle.RetryAfterSeconds
                        });
                    }
                }
            }
            else if (admission.Outcome == QueryCostOutcome.Admitted)
            {
                HttpResponseGuard.SetHeader(httpContext, "X-Query-Cost", calculatedCost.ToString(CultureInfo.InvariantCulture));
                HttpResponseGuard.SetHeader(httpContext, "X-RateLimit-Remaining", admission.RemainingTokens.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// settlement of a failed, aborted or partially failed request. The failure fee
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

        return (int)Math.Clamp(totalComputed, baseCost, Math.Max(baseCost, policy.MaxCostPerQuery));
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
                    if (TableIdentifier.TryCreate(parts[0], "public", parts[1], out var qualified))
                    {
                        return qualified;
                    }
                }
                else if (TableIdentifier.TryCreate("graphql", "public", val, out var plain))
                {
                    return plain;
                }
            }
        }
        return new TableIdentifier("graphql", "query", "graphql");
    }
}
