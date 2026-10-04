namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class DifferentialPrivacyEngine(ILogger<DifferentialPrivacyEngine>? logger = null) : IDifferentialPrivacyEngine
{
    private const double DefaultDailyEpsilonBudget = 10.0;
    private readonly ConcurrentDictionary<string, ClientBudgetState> _budgets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<DifferentialPrivacyEngine> _logger = logger ?? NullLogger<DifferentialPrivacyEngine>.Instance;

    public ValueTask<PrivacyBudget> GetBudgetAsync(string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var state = GetOrCreateState(clientId);
        lock (state)
        {
            state.CheckAndApplyDailyRollOver();
            return ValueTask.FromResult(new PrivacyBudget(
                ClientId: clientId,
                TotalDailyEpsilonBudget: state.TotalDailyBudget,
                ConsumedEpsilon: state.ConsumedEpsilon,
                LastResetUtc: state.LastResetUtc
            ));
        }
    }

    public ValueTask ResetBudgetAsync(string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var state = GetOrCreateState(clientId);
        lock (state)
        {
            state.ConsumedEpsilon = 0.0;
            state.LastResetUtc = DateTimeOffset.UtcNow;
        }

        _logger.LogInformation("Epsilon privacy budget manually reset for client '{ClientId}'.", clientId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<DifferentialPrivacyPerturbationResult> PerturbAsync(
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

        var state = GetOrCreateState(request.ClientId);

        // 1. Check small-cohort suppression (k-Anonymity Guardrail)
        if (request.CohortCount.HasValue && request.CohortCount.Value < request.MinimumCohortSize)
        {
            lock (state)
            {
                state.CheckAndApplyDailyRollOver();
                return ValueTask.FromResult(new DifferentialPrivacyPerturbationResult(
                    ClientId: request.ClientId,
                    OriginalValue: request.Value,
                    PerturbedValue: null,
                    Noise: 0.0,
                    IsSuppressed: true,
                    SuppressionReason: $"Cohort size ({request.CohortCount.Value}) is below k-anonymity threshold ({request.MinimumCohortSize}).",
                    ConsumedEpsilon: state.ConsumedEpsilon,
                    RemainingEpsilon: Math.Max(0.0, state.TotalDailyBudget - state.ConsumedEpsilon),
                    Timestamp: DateTimeOffset.UtcNow
                ));
            }
        }

        // 2. Dynamic Epsilon Budget Verification & Deduction
        double consumed;
        double remaining;
        lock (state)
        {
            state.CheckAndApplyDailyRollOver();
            var projected = state.ConsumedEpsilon + request.Epsilon;
            if (projected > state.TotalDailyBudget)
            {
                _logger.LogWarning(
                    "Client '{ClientId}' exhausted daily privacy budget. Attempted to consume {Attempted:F2} with {Consumed:F2}/{Total:F2} already consumed.",
                    request.ClientId, request.Epsilon, state.ConsumedEpsilon, state.TotalDailyBudget);

                throw new PrivacyBudgetExhaustedException(request.ClientId, projected, state.TotalDailyBudget);
            }

            state.ConsumedEpsilon = projected;
            consumed = state.ConsumedEpsilon;
            remaining = Math.Max(0.0, state.TotalDailyBudget - state.ConsumedEpsilon);
        }

        // 3. Cryptographically secure Laplace Noise Perturbation (Inverse-CDF method)
        // b = Delta f / epsilon
        var scale = request.Sensitivity / request.Epsilon;
        var noise = GenerateLaplaceNoise(scale);
        var perturbedValue = Math.Round(request.Value + noise, 4);

        return ValueTask.FromResult(new DifferentialPrivacyPerturbationResult(
            ClientId: request.ClientId,
            OriginalValue: request.Value,
            PerturbedValue: perturbedValue,
            Noise: Math.Round(noise, 4),
            IsSuppressed: false,
            SuppressionReason: null,
            ConsumedEpsilon: consumed,
            RemainingEpsilon: remaining,
            Timestamp: DateTimeOffset.UtcNow
        ));
    }

    private const int MaxTrackedClients = 10_000;

    private ClientBudgetState GetOrCreateState(string clientId)
    {
        if (_budgets.TryGetValue(clientId, out var existing))
        {
            return existing;
        }

        if (_budgets.Count >= MaxTrackedClients)
        {
            // Evict stale clients older than 48 hours
            var cutoff = DateTimeOffset.UtcNow.AddHours(-48);
            foreach (var (key, state) in _budgets)
            {
                if (state.LastResetUtc < cutoff)
                {
                    _budgets.TryRemove(key, out _);
                }
            }

            if (_budgets.Count >= MaxTrackedClients)
            {
                throw new InvalidOperationException($"Differential privacy budget tracker exceeded maximum capacity of {MaxTrackedClients} clients.");
            }
        }

        return _budgets.GetOrAdd(clientId, id => new ClientBudgetState(id, DefaultDailyEpsilonBudget));
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

    private sealed class ClientBudgetState(string clientId, double totalDailyBudget)
    {
        public string ClientId { get; } = clientId;
        public double TotalDailyBudget { get; set; } = totalDailyBudget;
        public double ConsumedEpsilon { get; set; }
        public DateTimeOffset LastResetUtc { get; set; } = DateTimeOffset.UtcNow;

        public void CheckAndApplyDailyRollOver()
        {
            var now = DateTimeOffset.UtcNow;
            if (now.Date > LastResetUtc.Date)
            {
                ConsumedEpsilon = 0.0;
                LastResetUtc = now;
            }
        }
    }
}
