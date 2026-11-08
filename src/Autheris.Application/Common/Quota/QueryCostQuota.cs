namespace Autheris.Application.Common.Quota;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.State.Caching.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class QueryCostQuota : IQueryCostQuota
{
    private readonly IClientTierResolver _tierResolver;
    private readonly IRateLimiterService _rateLimiter;
    private readonly IOptions<GatewayOptions> _gatewayOptions;

    public QueryCostQuota(
        IClientTierResolver tierResolver,
        IRateLimiterService rateLimiter,
        IOptions<GatewayOptions> gatewayOptions)
    {
        _tierResolver = tierResolver ?? throw new ArgumentNullException(nameof(tierResolver));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
    }

    public async Task<QueryCostAdmission> AdmitAsync(QueryCostRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Resolve client tier and quota policy
        // If tier resolution throws, AdmitAsync propagates the exception and never returns Unmetered
        var clientContext = await _tierResolver.ResolveAsync(
            request.Principal, request.ApiKey, request.ClientIp, ct).ConfigureAwait(false);

        var policy = clientContext.Policy;
        int maxAllowed = request.CapByTierMaxCostPerQuery
            ? Math.Min(policy.MaxCostPerQuery, request.TransportMaxCost)
            : request.TransportMaxCost;

        // Further capped at policy.MaxTokensCapacity (a statement that exceeds capacity is OverCost, not an endless 429)
        maxAllowed = Math.Min(maxAllowed, policy.MaxTokensCapacity);

        // Check if estimated cost exceeds maximum allowed cost
        if (request.EstimatedCost > maxAllowed)
        {
            return new QueryCostAdmission(
                QueryCostOutcome.OverCost,
                clientContext.SubjectId,
                policy,
                ReservedCost: 0,
                MaxAllowedCost: maxAllowed,
                RemainingTokens: 0,
                RetryAfterSeconds: 0,
                Scope: clientContext.ResolvedScope);
        }

        // Unmetered only when rate limiting is disabled by configuration (ceiling still applies)
        if (_gatewayOptions.Value?.IsRateLimitingDisabled == true)
        {
            return new QueryCostAdmission(
                QueryCostOutcome.Unmetered,
                clientContext.SubjectId,
                policy,
                ReservedCost: 0,
                MaxAllowedCost: maxAllowed,
                RemainingTokens: 0,
                RetryAfterSeconds: 0,
                Scope: clientContext.ResolvedScope);
        }

        // 2. Reserve cost quota
        // If reservation throws, AdmitAsync propagates the exception and never returns Unmetered
        var limitResult = await _rateLimiter.ReserveCostQuotaAsync(
            clientContext.SubjectId,
            request.EstimatedCost,
            policy,
            ct).ConfigureAwait(false);

        if (!limitResult.Allowed)
        {
            return new QueryCostAdmission(
                QueryCostOutcome.QuotaExhausted,
                clientContext.SubjectId,
                policy,
                ReservedCost: 0,
                MaxAllowedCost: maxAllowed,
                RemainingTokens: limitResult.RemainingTokens,
                RetryAfterSeconds: limitResult.RetryAfterSeconds,
                Scope: clientContext.ResolvedScope);
        }

        return new QueryCostAdmission(
            QueryCostOutcome.Admitted,
            clientContext.SubjectId,
            policy,
            ReservedCost: request.EstimatedCost,
            MaxAllowedCost: maxAllowed,
            RemainingTokens: limitResult.RemainingTokens,
            RetryAfterSeconds: 0,
            Scope: clientContext.ResolvedScope);
    }

    public async Task<CostQuotaResult?> SettleAsync(QueryCostAdmission admission, int actualCost, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.Outcome != QueryCostOutcome.Admitted || !admission.TryMarkSettled())
        {
            return null;
        }

        return await _rateLimiter.SettleCostQuotaAsync(
            admission.SubjectId,
            admission.ReservedCost,
            actualCost,
            admission.Policy,
            ct).ConfigureAwait(false);
    }

    public async Task ChargeFailureFeeAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var queryCostOptions = _gatewayOptions.Value?.QueryCost;
        int failureFee = Math.Max(queryCostOptions?.BaseCost ?? 1, queryCostOptions?.FailureFee ?? 1);

        var clientContext = await _tierResolver.ResolveAsync(principal, null, null, ct).ConfigureAwait(false);

        // Settle-only debit of failureFee without a reservation check
        await _rateLimiter.SettleCostQuotaAsync(
            clientContext.SubjectId,
            0,
            failureFee,
            clientContext.Policy,
            ct).ConfigureAwait(false);
    }
}
