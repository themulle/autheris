namespace Autheris.Application.Jobs.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Runs async jobs in parallel: at most <c>maxConcurrent</c> overall and <c>maxPerTenant</c> per tenant. Jobs of a
/// tenant at its cap wait in a per-tenant queue. A freed slot always goes back to the dispatch loop, which files all
/// readable work under its tenant and then serves the tenants round-robin. A finished job releases its per-tenant count and its global slot; nothing is handed over.
/// </summary>
internal sealed class AsyncJobScheduler : IDisposable
{
    private static readonly Meter Meter = new("Autheris.AsyncJobs");
    private static readonly UpDownCounter<long> RunningGauge = Meter.CreateUpDownCounter<long>("autheris.asyncjobs.running", description: "Async jobs currently executing on this node.");
    private static readonly UpDownCounter<long> PendingGauge = Meter.CreateUpDownCounter<long>("autheris.asyncjobs.pending", description: "Async jobs waiting for a per-tenant slot on this node.");
    private static readonly Counter<long> DiscardedCounter = Meter.CreateCounter<long>("autheris.asyncjobs.discarded", description: "Async jobs cancelled before start because the node shut down.");

    private readonly int _maxPerTenant;
    private readonly Func<AsyncJobExecutionItem, CancellationToken, Task> _run;
    private readonly Func<AsyncJobExecutionItem, Task>? _onDiscard;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _global;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<AsyncJobExecutionItem>> _pending = new(StringComparer.Ordinal);
    private readonly Queue<string> _rotation = new();
    private readonly HashSet<Task> _tracked = [];
    private TaskCompletionSource _eligible = NewSignal();

    public AsyncJobScheduler(
        int maxConcurrent,
        int maxPerTenant,
        Func<AsyncJobExecutionItem, CancellationToken, Task> run,
        ILogger logger,
        Func<AsyncJobExecutionItem, Task>? onDiscard = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerTenant, 1);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(logger);
        _global = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        _maxPerTenant = Math.Min(maxPerTenant, maxConcurrent);
        _run = run;
        _logger = logger;
        _onDiscard = onDiscard;
    }

    public void Dispose() => _global.Dispose();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RunAsync(ChannelReader<AsyncJobExecutionItem> reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        try
        {
            while (true)
            {
                await _global.WaitAsync(ct).ConfigureAwait(false);
                AsyncJobExecutionItem? item;
                try
                {
                    item = await NextItemAsync(reader, ct).ConfigureAwait(false);
                }
                catch
                {
                    _global.Release();
                    throw;
                }

                if (item is null)
                {
                    _global.Release();
                    return;
                }

                Start(item, ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Async job scheduler stopping.");
        }
        finally
        {
            await DrainAsync(reader, ct.IsCancellationRequested).ConfigureAwait(false);
        }
    }

    /// <summary>With a global slot held: files new channel work per tenant, then serves tenants round-robin; null when the channel is done and nothing waits.</summary>
    private async Task<AsyncJobExecutionItem?> NextItemAsync(ChannelReader<AsyncJobExecutionItem> reader, CancellationToken ct)
    {
        var channelDone = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task eligible;
            lock (_gate)
            {
                // Everything readable is filed under its tenant first; the slot is then given round-robin, so neither a
                // backlog nor a flood of fresh items can keep another tenant waiting longer than one rotation.
                while (reader.TryRead(out var fresh))
                {
                    Enqueue(fresh);
                }

                var waiting = TakePending();
                if (waiting is not null)
                {
                    return waiting;
                }

                if (channelDone && _pending.Count == 0)
                {
                    return null;
                }

                eligible = _eligible.Task;
            }

            if (channelDone)
            {
                await eligible.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var readable = reader.WaitToReadAsync(linked.Token).AsTask();
            var first = await Task.WhenAny(readable, eligible).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            if (first == readable)
            {
                try
                {
                    channelDone = !await readable.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                }
            }
            else
            {
                try { await readable.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private bool UnderCap(string tenant) => _running.GetValueOrDefault(tenant) < _maxPerTenant;

    private void Enqueue(AsyncJobExecutionItem item)
    {
        var tenant = item.Job.TenantId;
        if (!_pending.TryGetValue(tenant, out var queue))
        {
            _pending[tenant] = queue = new Queue<AsyncJobExecutionItem>();
            _rotation.Enqueue(tenant);
        }

        queue.Enqueue(item);
        PendingGauge.Add(1);
    }

    /// <summary>Takes the next waiting item round-robin from a tenant that is under its cap (caller holds the lock).</summary>
    private AsyncJobExecutionItem? TakePending()
    {
        for (var i = _rotation.Count; i > 0; i--)
        {
            var tenant = _rotation.Dequeue();
            var queue = _pending[tenant];
            if (!UnderCap(tenant))
            {
                _rotation.Enqueue(tenant);
                continue;
            }

            var item = queue.Dequeue();
            PendingGauge.Add(-1);
            if (queue.Count == 0)
            {
                _pending.Remove(tenant);
            }
            else
            {
                _rotation.Enqueue(tenant);
            }

            _running[tenant] = _running.GetValueOrDefault(tenant) + 1;
            return item;
        }

        return null;
    }

    private void Start(AsyncJobExecutionItem item, CancellationToken ct)
    {
        RunningGauge.Add(1);
        lock (_gate)
        {
            var task = Task.Run(() => RunTrackedAsync(item, ct), CancellationToken.None);
            _tracked.Add(task);
            _ = task.ContinueWith(t => { lock (_gate) { _tracked.Remove(t); } }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task RunTrackedAsync(AsyncJobExecutionItem item, CancellationToken ct)
    {
        try
        {
            await _run(item, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Async job {JobId} cancelled by shutdown.", item.Job.JobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Async job {JobId} failed in the scheduler.", item.Job.JobId);
        }
        finally
        {
            var tenant = item.Job.TenantId;
            RunningGauge.Add(-1);
            lock (_gate)
            {
                var left = _running.GetValueOrDefault(tenant) - 1;
                if (left <= 0)
                {
                    _running.Remove(tenant);
                }
                else
                {
                    _running[tenant] = left;
                }

                // A waiting tenant may be eligible again: wake the dispatch loop if it is waiting for work.
                var signal = _eligible;
                _eligible = NewSignal();
                signal.TrySetResult();
            }

            _global.Release();
        }
    }

    private async Task DrainAsync(ChannelReader<AsyncJobExecutionItem> reader, bool shuttingDown)
    {
        Task[] tasks;
        lock (_gate)
        {
            tasks = [.. _tracked];
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        List<AsyncJobExecutionItem> discarded = [];
        lock (_gate)
        {
            foreach (var queue in _pending.Values)
            {
                discarded.AddRange(queue);
            }

            PendingGauge.Add(-_pending.Values.Sum(q => q.Count));
            _pending.Clear();
            _rotation.Clear();
            if (shuttingDown)
            {
                // Items still in the channel would be lost with the process: they are cancelled too.
                while (reader.TryRead(out var queued))
                {
                    discarded.Add(queued);
                }
            }
        }

        DiscardedCounter.Add(discarded.Count);
        if (_onDiscard is null)
        {
            return;
        }

        foreach (var item in discarded)
        {
            try
            {
                await _onDiscard(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not cancel queued async job {JobId} on shutdown.", item.Job.JobId);
            }
        }
    }
}
