namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Quota;
using Autheris.Application.State.Caching.Interfaces;
using Autheris.Application.Usage;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RequestContext = HotChocolate.Execution.RequestContext;

public sealed partial class CostAndQuotaMiddleware
{
    private readonly record struct ScopeGuardOutcome(int ActualCost, bool ShortCircuit);

    internal static void WriteScopeConcurrencyError(
        RequestContext context,
        HttpContext httpContext,
        ScopeConcurrencyExceededException ex)
    {
        var errorBuilder = ErrorBuilder.New()
            .SetMessage("Too many concurrent queries are active for this client scope. Retry later.")
            .SetCode("SCOPE_CONCURRENCY_EXCEEDED")
            .SetExtension("retryAfterSeconds", ex.RetryAfterSeconds);

        context.Result = OperationResult.FromError(errorBuilder.Build());
        HttpResponseGuard.SetStatus(httpContext, StatusCodes.Status429TooManyRequests);
        HttpResponseGuard.SetHeader(httpContext, "Retry-After", ex.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
    }

    internal static void WriteScopeTimeoutError(RequestContext context, HttpContext httpContext)
    {
        var error = ErrorBuilder.New()
            .SetMessage("The query exceeded the maximum allowed execution duration.")
            .SetCode("TIMEOUT")
            .Build();

        context.Result = OperationResult.FromError(error);
        HttpResponseGuard.SetStatus(httpContext, StatusCodes.Status504GatewayTimeout);
    }

    private async Task ExecuteDisabledRateLimitingAsync(
        RequestContext context,
        HttpContext httpContext,
        DocumentNode doc,
        IServiceProvider? services,
        GatewayOptions gatewayOptions)
    {
        var disabledPrincipal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.ResolveOrNull(context, httpContext);
        string? disabledApiKey = httpContext.Request.Headers.TryGetValue("X-API-Key", out var dak) ? dak.ToString() : null;
        string? disabledIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var disabledScopeGuard = services?.GetService<IScopeQueryExecutionGuard>() ?? NullScopeQueryExecutionGuard.Instance;

        try
        {
            await disabledScopeGuard.RunAsync(disabledPrincipal, disabledApiKey, disabledIp, async (_, scopedCt) =>
            {
                // the validation pass prices a variable page size without its value, so the runtime pass must still bound it here.
                var (rejected, hints) = await RejectIfOverGatewayComplexityAsync(context, doc, gatewayOptions, httpContext)
                    .ConfigureAwait(false);
                if (rejected)
                {
                    return false;
                }

                await ExecuteNextWithScopedAbortedAsync(httpContext, context, scopedCt).ConfigureAwait(false);
                if (!scopedCt.IsCancellationRequested && context.Result is OperationResult disabledOp)
                {
                    AttachIndexHintsToResult(disabledOp, hints);
                }

                return true;
            }, context.RequestAborted).ConfigureAwait(false);
        }
        catch (ScopeConcurrencyExceededException scopeEx)
        {
            WriteScopeConcurrencyError(context, httpContext, scopeEx);
        }
        catch (TimeoutException)
        {
            WriteScopeTimeoutError(context, httpContext);
        }
    }

    private async Task<ScopeGuardOutcome> ExecuteWithScopeGuardAsync(
        RequestContext context,
        HttpContext httpContext,
        IScopeQueryExecutionGuard scopeGuard,
        ClaimsPrincipal? principal,
        string? apiKey,
        string? clientIp,
        DocumentNode doc,
        IServiceProvider? services,
        GatewayOptions? gatewayOptions,
        QueryCostAdmission admission,
        int calculatedCost,
        IReadOnlyList<GraphQlIndexHint>? indexHints)
    {
        int actualCost = calculatedCost;
        try
        {
            await scopeGuard.RunAsync(principal, apiKey, clientIp, async (_, scopedCt) =>
            {
                await ExecuteNextWithScopedAbortedAsync(httpContext, context, scopedCt).ConfigureAwait(false);
                if (scopedCt.IsCancellationRequested ||
                    context.Result is not OperationResult op ||
                    (op.Errors is not null && op.Errors.Count > 0))
                {
                    // Mandatory minimum fee on client abort or execution failure.
                    actualCost = ComputeFailedCost(context, gatewayOptions?.QueryCost, admission.Policy);
                }
                else
                {
                    actualCost = ComputeActualCost(context, gatewayOptions?.QueryCost, admission.Policy);
                    AttachIndexHintsToResult(op, indexHints);
                }

                return true;
            }, context.RequestAborted).ConfigureAwait(false);

            return new ScopeGuardOutcome(actualCost, ShortCircuit: false);
        }
        catch (ScopeConcurrencyExceededException scopeEx)
        {
            TryRecordQuotaDenialUsage(services, httpContext, principal, doc, UsageOutcome.DenyRateLimit, calculatedCost);
            WriteScopeConcurrencyError(context, httpContext, scopeEx);
            return new ScopeGuardOutcome(ComputeFailedCost(context, gatewayOptions?.QueryCost, admission.Policy), ShortCircuit: true);
        }
        catch (TimeoutException)
        {
            WriteScopeTimeoutError(context, httpContext);
            return new ScopeGuardOutcome(ComputeFailedCost(context, gatewayOptions?.QueryCost, admission.Policy), ShortCircuit: true);
        }
        catch
        {
            // Mandatory minimum fee on exception (rows already produced are still charged).
            actualCost = ComputeFailedCost(context, gatewayOptions?.QueryCost, admission.Policy);
            throw;
        }
    }

    private async Task ExecuteNextWithScopedAbortedAsync(HttpContext httpContext, RequestContext context, CancellationToken scopedCt)
    {
        var callerAborted = context.RequestAborted;
        var previousHttpAborted = httpContext.RequestAborted;
        httpContext.RequestAborted = scopedCt;
        try
        {
            await _next(context).ConfigureAwait(false);
            if (scopedCt.IsCancellationRequested && !callerAborted.IsCancellationRequested)
            {
                throw new TimeoutException("The query exceeded the maximum allowed execution duration for this scope.");
            }
        }
        finally
        {
            httpContext.RequestAborted = previousHttpAborted;
        }
    }
}
