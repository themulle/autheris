namespace Autheris.Application.Governance.Services;

using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// AR-04: High-performance Differential Privacy engine with atomic cluster-wide epsilon budget tracking.
/// Uses atomic Redis Lua / InMemory Check-and-Consume counters with ceiling micro-units to prevent TOCTOU and drift.
/// Fails closed when cluster budget store is unavailable.
/// </summary>
public sealed class DifferentialPrivacyEngine : IDifferentialPrivacyEngine
{
    private const double DefaultDailyEpsilonBudget = 10.0;
    private const long MicroUnits = 1_000_000L;
    private static readonly TimeSpan BudgetTtl = TimeSpan.FromHours(48);

    private readonly IDistributedClusterStateProvider _clusterState;
    private readonly IAuditLogRepository? _auditLog;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DifferentialPrivacyEngine> _logger;

    public DifferentialPrivacyEngine(
        IDistributedClusterStateProvider? clusterState = null,
        IAuditLogRepository? auditLog = null,
        TimeProvider? timeProvider = null,
        ILogger<DifferentialPrivacyEngine>? logger = null)
    {
        _clusterState = clusterState ?? new InMemoryClusterStateProvider();
        _auditLog = auditLog;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<DifferentialPrivacyEngine>.Instance;
    }

    public async ValueTask<PrivacyBudget> GetBudgetAsync(string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var key = GetBudgetKey(clientId);
        long consumedMicro = 0;
        try
        {
            var stored = await _clusterState.GetAsync<long>(key, cancellationToken).ConfigureAwait(false);
            consumedMicro = stored;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read DP budget from cluster store for client '{ClientId}'.", clientId);
        }

        var consumed = consumedMicro / (double)MicroUnits;
        return new PrivacyBudget(
            ClientId: clientId,
            TotalDailyEpsilonBudget: DefaultDailyEpsilonBudget,
            ConsumedEpsilon: consumed,
            LastResetUtc: _timeProvider.GetUtcNow()
        );
    }

    public async ValueTask ResetBudgetAsync(string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var key = GetBudgetKey(clientId);
        await _clusterState.RemoveAsync(key, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Epsilon privacy budget manually reset for client '{ClientId}'.", clientId);

        if (_auditLog != null)
        {
            await RecordAuditAsync("DP_BUDGET_RESET", "ALLOW", clientId, new
            {
                action = "ResetBudget",
                resetAt = _timeProvider.GetUtcNow()
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<DifferentialPrivacyPerturbationResult> PerturbAsync(
        DifferentialPrivacyPerturbationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientId, nameof(request.ClientId));

        if (request.Epsilon <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Epsilon must be strictly positive (> 0).");
        }

        if (request.Sensitivity < 0.01)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Sensitivity (Delta f) must be at least 0.01 to ensure effective differential privacy noise.");
        }

        // 1. Check small-cohort suppression (k-Anonymity Guardrail)
        if (request.CohortCount.HasValue && request.CohortCount.Value < request.MinimumCohortSize)
        {
            var currentBudget = await GetBudgetAsync(request.ClientId, cancellationToken).ConfigureAwait(false);
            return new DifferentialPrivacyPerturbationResult(
                ClientId: request.ClientId,
                OriginalValue: request.Value,
                PerturbedValue: null,
                Noise: 0.0,
                IsSuppressed: true,
                SuppressionReason: $"Cohort size ({request.CohortCount.Value}) is below k-anonymity threshold ({request.MinimumCohortSize}).",
                ConsumedEpsilon: currentBudget.ConsumedEpsilon,
                RemainingEpsilon: currentBudget.RemainingEpsilon,
                Timestamp: _timeProvider.GetUtcNow()
            );
        }

        // 2. Dynamic Epsilon Budget Verification & Deduction via Atomic Cluster Counter
        var costMicro = (long)Math.Ceiling(request.Epsilon * MicroUnits);
        var limitMicro = (long)Math.Round(DefaultDailyEpsilonBudget * MicroUnits);
        var key = GetBudgetKey(request.ClientId);

        var (outcome, consumedAfterMicro) = await _clusterState.TryConsumeBudgetAsync(
            key, costMicro, limitMicro, BudgetTtl, cancellationToken).ConfigureAwait(false);

        if (outcome == BudgetConsumeOutcome.Exhausted)
        {
            var consumed = consumedAfterMicro / (double)MicroUnits;
            var attemptedTotal = (consumedAfterMicro + costMicro) / (double)MicroUnits;
            _logger.LogWarning(
                "Client '{ClientId}' exhausted daily privacy budget. Attempted to consume {Attempted:F2} with {Consumed:F2}/{Total:F2} already consumed.",
                request.ClientId, request.Epsilon, consumed, DefaultDailyEpsilonBudget);

            if (_auditLog != null)
            {
                await RecordAuditAsync("DP_BUDGET_EXHAUSTED", "DENY", request.ClientId, new
                {
                    attempted = request.Epsilon,
                    consumed,
                    limit = DefaultDailyEpsilonBudget
                }, cancellationToken).ConfigureAwait(false);
            }

            throw new PrivacyBudgetExhaustedException(request.ClientId, attemptedTotal, DefaultDailyEpsilonBudget);
        }

        if (outcome == BudgetConsumeOutcome.StoreUnavailable)
        {
            _logger.LogError("Differential privacy cluster budget store is unavailable for client '{ClientId}'. Fail-closed.", request.ClientId);

            if (_auditLog != null)
            {
                await RecordAuditAsync("DP_BUDGET_STORE_UNAVAILABLE", "DENY", request.ClientId, new
                {
                    attempted = request.Epsilon,
                    error = "Cluster budget store unavailable"
                }, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException($"Differential privacy budget store is unavailable for client '{request.ClientId}'. Failing closed.");
        }

        // 3. Cryptographically secure Laplace Noise Perturbation (Inverse-CDF method)
        var scale = request.Sensitivity / request.Epsilon;
        var noise = GenerateLaplaceNoise(scale);
        var perturbedValue = Math.Round(request.Value + noise, 4);
        var consumedEpsilon = consumedAfterMicro / (double)MicroUnits;
        var remainingEpsilon = Math.Max(0.0, DefaultDailyEpsilonBudget - consumedEpsilon);

        return new DifferentialPrivacyPerturbationResult(
            ClientId: request.ClientId,
            OriginalValue: request.Value,
            PerturbedValue: perturbedValue,
            Noise: Math.Round(noise, 4),
            IsSuppressed: false,
            SuppressionReason: null,
            ConsumedEpsilon: consumedEpsilon,
            RemainingEpsilon: remainingEpsilon,
            Timestamp: _timeProvider.GetUtcNow()
        );
    }

    private string GetBudgetKey(string clientId)
    {
        var dateUtc = _timeProvider.GetUtcNow().ToString("yyyyMMdd");
        return $"dp:budget:{clientId.Trim()}:{dateUtc}";
    }

    private static double GenerateLaplaceNoise(double scale)
    {
        // Sample uniform u in (-0.5, 0.5) using cryptographically secure RNG
        int randomInt = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        double u = ((double)randomInt / int.MaxValue) - 0.5;

        // Guard against exact zero
        if (Math.Abs(u) < 1e-12)
        {
            u = 1e-12;
        }

        // Laplace inverse CDF: x = -b * sgn(u) * ln(1 - 2|u|)
        return -scale * Math.Sign(u) * Math.Log(1.0 - (2.0 * Math.Abs(u)));
    }

    private async ValueTask RecordAuditAsync(string eventType, string decision, string clientId, object details, CancellationToken ct)
    {
        try
        {
            await _auditLog!.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = TenantId.LegacySingleTenant,
                EventType = eventType,
                ActorSid = new Sid(clientId),
                TargetTable = "DifferentialPrivacy",
                Decision = decision,
                TraceId = string.Empty,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(details)
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record audit event {EventType} for DP", eventType);
        }
    }
}
