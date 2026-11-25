namespace Autheris.Application.Common.Quota;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.State.Caching.Interfaces;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

/// <summary>
/// Active per-scope execution caps resolved for the current query execution flow.
/// </summary>
public readonly record struct ScopeExecutionLease(
    string SubjectId,
    string? Scope,
    ClientQuotaPolicy Policy,
    CancellationToken CancellationToken,
    bool IsNested = false)
{
    public int? MaxConcurrentQueries => Policy?.MaxConcurrentQueries;
    public int? MaxQueryDurationSeconds => Policy?.MaxQueryDurationSeconds;
    public int? MaxResultRows => Policy?.MaxResultRows;

    public int ClampRows(int currentMaxRows) =>
        MaxResultRows is > 0 and var scopedMax
            ? (currentMaxRows > 0 ? Math.Min(currentMaxRows, scopedMax) : scopedMax)
            : currentMaxRows;

    public long ClampRows(long currentMaxRows) =>
        MaxResultRows is > 0 and var scopedMax
            ? (currentMaxRows > 0 ? Math.Min(currentMaxRows, scopedMax) : scopedMax)
            : currentMaxRows;

    public int ClampTimeoutSeconds(int currentTimeoutSeconds) =>
        MaxQueryDurationSeconds is > 0 and var scopedDuration
            ? (currentTimeoutSeconds > 0 ? Math.Min(currentTimeoutSeconds, scopedDuration) : scopedDuration)
            : currentTimeoutSeconds;

    public SqlRowLimit ClampRowLimit(SqlRowLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return MaxResultRows is > 0 and var scopedMax ? limit.CappedAt(scopedMax) : limit;
    }
}

/// <summary>
/// Enforces per-scope and per-tier concurrency, duration, and result-row execution limits across gateway read paths.
/// Uses an ambient async context so nested pipeline calls share the outer concurrency slot without double-counting.
/// </summary>
public interface IScopeQueryExecutionGuard
{
    Task RunAsync(
        ClaimsPrincipal? principal,
        string? apiKey,
        string? clientIp,
        Func<ScopeExecutionLease, CancellationToken, Task> work,
        CancellationToken callerToken = default);

    Task<T> RunAsync<T>(
        ClaimsPrincipal? principal,
        string? apiKey,
        string? clientIp,
        Func<ScopeExecutionLease, CancellationToken, Task<T>> work,
        CancellationToken callerToken = default);

    bool TryGetAmbient(out ScopeExecutionLease lease);
}

public sealed class ScopeQueryExecutionGuard : IScopeQueryExecutionGuard
{
    internal const int MaxTrackedSubjects = 50_000;

    private static readonly AsyncLocal<AmbientState?> Ambient = new();

    private readonly IClientTierResolver _tierResolver;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SubjectSlot> _slots = new(StringComparer.Ordinal);

    private sealed record AmbientCaller(string? ClientIp, string? ApiKey);
    private static readonly AsyncLocal<AmbientCaller?> AmbientCallerContext = new();

    private sealed class CallerContextScope(AmbientCaller? previous) : IDisposable
    {
        public void Dispose() => AmbientCallerContext.Value = previous;
    }

    public static IDisposable BeginCallerContext(string? clientIp, string? apiKey = null)
    {
        var previous = AmbientCallerContext.Value;
        AmbientCallerContext.Value = new AmbientCaller(clientIp, apiKey);
        return new CallerContextScope(previous);
    }

    internal static string? ResolveClientIp(Autheris.Application.Common.Interfaces.IClientIpResolver? resolver)
    {
        var ip = resolver?.ResolveClientIp();
        return ip is not null && !ip.Equals(System.Net.IPAddress.None) ? ip.ToString() : null;
    }

    public ScopeQueryExecutionGuard(IClientTierResolver tierResolver, TimeProvider? timeProvider = null)
    {
        _tierResolver = tierResolver ?? throw new ArgumentNullException(nameof(tierResolver));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryGetAmbient(out ScopeExecutionLease lease)
    {
        var state = Ambient.Value;
        if (state is not null && ReferenceEquals(state.Owner, this))
        {
            lease = state.Lease;
            return true;
        }

        lease = default;
        return false;
    }

    public static bool TryGetCurrent(out ScopeExecutionLease lease)
    {
        var state = Ambient.Value;
        if (state is not null)
        {
            lease = state.Lease;
            return true;
        }

        lease = default;
        return false;
    }

    public Task RunAsync(
        ClaimsPrincipal? principal,
        string? apiKey,
        string? clientIp,
        Func<ScopeExecutionLease, CancellationToken, Task> work,
        CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(
            principal,
            apiKey,
            clientIp,
            async (lease, scopedCt) =>
            {
                await work(lease, scopedCt).ConfigureAwait(false);
                return true;
            },
            callerToken);
    }

    public async Task<T> RunAsync<T>(
        ClaimsPrincipal? principal,
        string? apiKey,
        string? clientIp,
        Func<ScopeExecutionLease, CancellationToken, Task<T>> work,
        CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        var existing = Ambient.Value;
        if (existing is not null && ReferenceEquals(existing.Owner, this))
        {
            callerToken.ThrowIfCancellationRequested();
            using var childLinkedCts = existing.Lease.CancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(callerToken, existing.Lease.CancellationToken)
                : null;
            var childCt = childLinkedCts?.Token ?? callerToken;
            var childLease = existing.Lease with { CancellationToken = childCt, IsNested = true };
            try
            {
                return await work(childLease, childCt).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                existing.DurationCts is { IsCancellationRequested: true } &&
                !callerToken.IsCancellationRequested &&
                (ex is OperationCanceledException || ex is System.Data.Common.DbException))
            {
                throw new TimeoutException("The query exceeded the maximum allowed execution duration for this scope.", ex);
            }
        }

        callerToken.ThrowIfCancellationRequested();

        var ambientCaller = AmbientCallerContext.Value;
        var effectiveApiKey = !string.IsNullOrWhiteSpace(apiKey) ? apiKey : ambientCaller?.ApiKey;
        var effectiveClientIp = !string.IsNullOrWhiteSpace(clientIp) ? clientIp : ambientCaller?.ClientIp;
        if (principal?.Identity?.IsAuthenticated != true &&
            string.IsNullOrWhiteSpace(effectiveApiKey) &&
            string.IsNullOrWhiteSpace(effectiveClientIp))
        {
            effectiveClientIp = "unassigned";
        }

        var clientContext = await _tierResolver.ResolveAsync(principal, effectiveApiKey, effectiveClientIp, callerToken).ConfigureAwait(false);
        var policy = clientContext.Policy;
        var subjectId = clientContext.SubjectId;
        var scope = clientContext.ResolvedScope;

        SubjectSlot? acquiredSlot = null;
        if (policy.MaxConcurrentQueries is > 0 and var maxConcurrent)
        {
            acquiredSlot = AcquireSlot(subjectId, maxConcurrent, scope);
        }

        CancellationTokenSource? durationCts = null;
        CancellationTokenSource? linkedCts = null;
        var previous = Ambient.Value;

        try
        {
            var effectiveCt = callerToken;
            if (policy.MaxQueryDurationSeconds is > 0 and var maxDurationSeconds)
            {
                durationCts = new CancellationTokenSource(TimeSpan.FromSeconds(maxDurationSeconds), _timeProvider);
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, durationCts.Token);
                effectiveCt = linkedCts.Token;
            }

            var lease = new ScopeExecutionLease(subjectId, scope, policy, effectiveCt, IsNested: false);
            Ambient.Value = new AmbientState(this, lease, durationCts);
            try
            {
                return await work(lease, effectiveCt).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                durationCts is { IsCancellationRequested: true } &&
                !callerToken.IsCancellationRequested &&
                (ex is OperationCanceledException || ex is System.Data.Common.DbException))
            {
                throw new TimeoutException("The query exceeded the maximum allowed execution duration for this scope.", ex);
            }
        }
        finally
        {
            Ambient.Value = previous;
            if (acquiredSlot is not null)
            {
                ReleaseSlot(subjectId, acquiredSlot);
            }

            linkedCts?.Dispose();
            durationCts?.Dispose();
        }
    }

    private SubjectSlot AcquireSlot(string subjectId, int maxConcurrentQueries, string? scope)
    {
        while (true)
        {
            if (_slots.TryGetValue(subjectId, out var existing))
            {
                lock (existing.Gate)
                {
                    if (!existing.Removed)
                    {
                        if (existing.Active >= maxConcurrentQueries)
                        {
                            throw new ScopeConcurrencyExceededException(maxConcurrentQueries, scope);
                        }

                        existing.Active++;
                        return existing;
                    }
                }

                continue;
            }

            if (_slots.Count >= MaxTrackedSubjects)
            {
                throw new ScopeConcurrencyExceededException(maxConcurrentQueries, scope);
            }

            var candidate = new SubjectSlot { Active = 1 };
            if (_slots.TryAdd(subjectId, candidate))
            {
                return candidate;
            }
        }
    }

    private void ReleaseSlot(string subjectId, SubjectSlot slot)
    {
        var shouldRemove = false;
        lock (slot.Gate)
        {
            slot.Active--;
            if (slot.Active <= 0)
            {
                slot.Active = 0;
                slot.Removed = true;
                shouldRemove = true;
            }
        }

        if (shouldRemove)
        {
            _slots.TryRemove(KeyValuePair.Create(subjectId, slot));
        }
    }

    private sealed class SubjectSlot
    {
        public object Gate { get; } = new();
        public int Active { get; set; }
        public bool Removed { get; set; }
    }

    private sealed record AmbientState(ScopeQueryExecutionGuard Owner, ScopeExecutionLease Lease, CancellationTokenSource? DurationCts = null);
}

public sealed class NullScopeQueryExecutionGuard : IScopeQueryExecutionGuard
{
    public static NullScopeQueryExecutionGuard Instance { get; } = new();

    public Task RunAsync(
        ClaimsPrincipal? principal,
        string? apiKey,
        string? clientIp,
        Func<ScopeExecutionLease, CancellationToken, Task> work,
        CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(
            principal,
            apiKey,
            clientIp,
            async (lease, scopedCt) =>
            {
                await work(lease, scopedCt).ConfigureAwait(false);
                return true;
            },
            callerToken);
    }

    public Task<T> RunAsync<T>(
        ClaimsPrincipal? principal,
        string? apiKey,
        string? clientIp,
        Func<ScopeExecutionLease, CancellationToken, Task<T>> work,
        CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (ScopeQueryExecutionGuard.TryGetCurrent(out var ambient))
        {
            return RunWithAmbientAsync(ambient, work, callerToken);
        }

        return work(new ScopeExecutionLease("anonymous", null, ClientQuotaPolicy.ForTier(ClientTier.Internal), callerToken), callerToken);
    }

    private static async Task<T> RunWithAmbientAsync<T>(
        ScopeExecutionLease ambient,
        Func<ScopeExecutionLease, CancellationToken, Task<T>> work,
        CancellationToken callerToken = default)
    {
        using var childLinkedCts = ambient.CancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(callerToken, ambient.CancellationToken)
            : null;
        var childCt = childLinkedCts?.Token ?? callerToken;
        var childLease = ambient with { CancellationToken = childCt, IsNested = true };
        return await work(childLease, childCt).ConfigureAwait(false);
    }

    public bool TryGetAmbient(out ScopeExecutionLease lease)
    {
        lease = default;
        return false;
    }
}
