namespace Autheris.Application.FinOps.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.FinOps.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-AI-08: In-memory and distributed implementation of FOCUS-compliant FinOps Accounting.
/// Standardizes token and compute metering into FOCUS v1.2 records and enforces budget governance.
/// </summary>
public sealed class FocusCostAccountingService : IFinOpsAccountingService
{
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly IDistributedClusterStateProvider? _clusterState;
    private readonly ILogger<FocusCostAccountingService> _logger;

    private readonly ConcurrentDictionary<string, decimal> _localSpend = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<FocusCostRecord> _records = new();
    private const int MaxInMemoryRecords = 10000;
    private const decimal MicroUnits = 1_000_000m;
    private static readonly TimeSpan SharedCounterTtl = TimeSpan.FromDays(40);

    // E-14: the budget is a cluster-wide monthly counter (micro-EUR) in the shared state store.
    // _localSpend only holds what could not be written to the shared store (store unreachable), or everything when none is configured.
    private static string PeriodKey(string tenantId) =>
        $"finops:spend:{DateTimeOffset.UtcNow:yyyyMM}:{tenantId.ToLowerInvariant()}";

    public FocusCostAccountingService(
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<FocusCostAccountingService> logger,
        IDistributedClusterStateProvider? clusterState = null)
    {
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clusterState = clusterState;
    }

    public bool IsEnabled => _gatewayOptions.Value.FinOps.Enabled;

    public async ValueTask RecordUsageAsync(
        string tenantId,
        string principalId,
        string operationName,
        string category,
        long promptTokens,
        long completionTokens,
        long computeMs,
        IReadOnlyDictionary<string, string>? tags = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            tenantId = "default";
        }

        var options = _gatewayOptions.Value.FinOps;
        var promptCost = (promptTokens / 1000.0m) * options.PricePerThousandPromptTokens;
        var completionCost = (completionTokens / 1000.0m) * options.PricePerThousandCompletionTokens;
        var computeCost = (computeMs / 1000.0m) * options.PricePerComputeSecond;
        var totalBilled = Math.Round(promptCost + completionCost + computeCost, 6);

        var shared = _clusterState != null
            ? await TryIncrementSharedAsync(tenantId, totalBilled, ct).ConfigureAwait(false)
            : null;
        if (shared == null)
        {
            _localSpend.AddOrUpdate(tenantId, totalBilled, (_, current) => current + totalBilled);
            _logger.LogWarning("F-AI-08 Recorded local fallback spend of {Cost} EUR for tenant {Tenant} due to unavailable shared cluster store.", totalBilled, tenantId);
        }

        var now = DateTimeOffset.UtcNow;
        var periodStart = now.AddMilliseconds(-Math.Max(1, computeMs)).ToString("O");
        var periodEnd = now.ToString("O");

        var recordTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["principal_id"] = principalId ?? "anonymous",
            ["prompt_tokens"] = promptTokens.ToString(),
            ["completion_tokens"] = completionTokens.ToString(),
            ["compute_ms"] = computeMs.ToString()
        };

        if (tags != null)
        {
            foreach (var (k, v) in tags)
            {
                recordTags[k] = v;
            }
        }

        var record = new FocusCostRecord(
            ChargePeriodStart: periodStart,
            ChargePeriodEnd: periodEnd,
            BilledCost: totalBilled,
            EffectiveCost: totalBilled,
            Currency: "EUR",
            ConsumedQuantity: promptTokens + completionTokens > 0 ? (promptTokens + completionTokens) : computeMs,
            ConsumedUnit: promptTokens + completionTokens > 0 ? "Tokens" : "Milliseconds",
            SubAccountId: tenantId,
            ResourceId: operationName ?? "unspecified_operation",
            ServiceName: "Autheris",
            PricingCategory: category ?? (promptTokens + completionTokens > 0 ? "AI-Inference" : "DatabaseCompute"),
            Tags: recordTags
        );

        _records.Enqueue(record);
        while (_records.Count > MaxInMemoryRecords && _records.TryDequeue(out _))
        {
            // prune oldest records
        }

        _logger.LogDebug(
            "F-AI-08 FinOps recorded usage for tenant {Tenant}: Billed {Cost} EUR (Tokens: {Tokens}, Compute: {ComputeMs}ms)",
            tenantId, totalBilled, promptTokens + completionTokens, computeMs);

        await ValueTask.CompletedTask;
    }

    private async ValueTask<long?> TryIncrementSharedAsync(string tenantId, decimal amount, CancellationToken ct)
    {
        var micro = (long)Math.Round(amount * MicroUnits, MidpointRounding.AwayFromZero);
        try
        {
            return await _clusterState!.IncrementAsync(PeriodKey(tenantId), micro, SharedCounterTtl, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "F-AI-08 shared FinOps counter unavailable for tenant {Tenant}; accounting locally.", tenantId);
            return null;
        }
    }

    public async ValueTask<BudgetStatus> CheckBudgetAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            tenantId = "default";
        }

        var options = _gatewayOptions.Value.FinOps;
        var budgetLimit = options.TenantMonthlyBudgets.TryGetValue(tenantId, out var limit)
            ? limit
            : options.DefaultMonthlyBudget;

        var currentSpend = _localSpend.TryGetValue(tenantId, out var spend) ? spend : 0m;
        var clusterStoreFailed = false;
        if (_clusterState != null)
        {
            try
            {
                var shared = await _clusterState.GetAsync<long>(PeriodKey(tenantId), ct).ConfigureAwait(false);
                currentSpend += shared / MicroUnits;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                clusterStoreFailed = true;
                _logger.LogWarning(ex, "F-AI-08 shared FinOps counter unreadable for tenant {Tenant}; failing closed on budget enforcement.", tenantId);
            }
        }

        var isExceeded = budgetLimit > 0 && (clusterStoreFailed || currentSpend >= budgetLimit);
        var isWarning = budgetLimit > 0 && (clusterStoreFailed || currentSpend >= budgetLimit * (decimal)options.SoftCapRatio);

        if (isExceeded)
        {
            _logger.LogWarning(
                "F-AI-08 Hard budget cap EXCEEDED for tenant {Tenant}! Current spend: {Spend} EUR, limit: {Limit} EUR",
                tenantId, currentSpend, budgetLimit);
        }
        else if (isWarning)
        {
            _logger.LogInformation(
                "F-AI-08 Soft budget cap reached for tenant {Tenant}! Current spend: {Spend} EUR, limit: {Limit} EUR (threshold: {Ratio:P0})",
                tenantId, currentSpend, budgetLimit, options.SoftCapRatio);
        }

        return await ValueTask.FromResult(new BudgetStatus(isExceeded, isWarning, currentSpend, budgetLimit, tenantId));
    }

    public async IAsyncEnumerable<FocusCostRecord> GetRecordsAsync(
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string? tenantId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var snapshot = _records.ToArray();
        foreach (var r in snapshot)
        {
            if (ct.IsCancellationRequested)
            {
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(tenantId) &&
                !string.Equals(r.SubAccountId, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (DateTimeOffset.TryParse(r.ChargePeriodEnd, out var end) &&
                end >= startTime && end <= endTime)
            {
                yield return r;
            }
        }

        await Task.CompletedTask;
    }

    public async ValueTask ResetSpendAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            tenantId = "default";
        }

        _localSpend[tenantId] = 0m;
        if (_clusterState != null)
        {
            await _clusterState.RemoveAsync(PeriodKey(tenantId), ct).ConfigureAwait(false);
        }
    }
}
