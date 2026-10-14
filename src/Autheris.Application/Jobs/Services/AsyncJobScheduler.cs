namespace Autheris.Application.Jobs.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Runs async jobs in parallel: at most <c>maxConcurrent</c> overall and <c>maxPerTenant</c> per tenant. A job of a
/// tenant that is at its cap waits in a per-tenant queue without holding a global slot, so one tenant can never starve
/// the others. When a job finishes, its slot is handed directly to the next waiting job of the same tenant.
/// </summary>
internal sealed class AsyncJobScheduler : IDisposable
{
    private readonly int _maxPerTenant;
    private readonly Func<AsyncJobExecutionItem, CancellationToken, Task> _run;
    private readonly Func<AsyncJobExecutionItem, Task>? _onDiscard;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _global;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<AsyncJobExecutionItem>> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _tracked = [];

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

    public async Task RunAsync(ChannelReader<AsyncJobExecutionItem> reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        try
        {
            while (true)
            {
                await _global.WaitAsync(ct).ConfigureAwait(false);
                AsyncJobExecutionItem item;
                try
                {
                    item = await reader.ReadAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    _global.Release();
                    throw;
                }

                Dispatch(item, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            await DrainAsync().ConfigureAwait(false);
        }
    }

    private void Dispatch(AsyncJobExecutionItem item, CancellationToken ct)
    {
        var tenant = item.Job.TenantId;
        lock (_gate)
        {
            _running.TryGetValue(tenant, out var running);
            if (running >= _maxPerTenant)
            {
                if (!_pending.TryGetValue(tenant, out var queue))
                {
                    _pending[tenant] = queue = new Queue<AsyncJobExecutionItem>();
                }

                queue.Enqueue(item);
                // The tenant is at its cap: the item must not occupy a global slot.
                _global.Release();
                return;
            }

            _running[tenant] = running + 1;
            var task = Task.Run(() => RunTrackedAsync(item, ct), CancellationToken.None);
            _tracked.Add(task);
            _ = task.ContinueWith(t => { lock (_gate) { _tracked.Remove(t); } }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task RunTrackedAsync(AsyncJobExecutionItem item, CancellationToken ct)
    {
        var current = item;
        while (true)
        {
            try
            {
                await _run(current, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Async job {JobId} failed in the scheduler.", current.Job.JobId);
            }

            var tenant = current.Job.TenantId;
            lock (_gate)
            {
                if (!ct.IsCancellationRequested && _pending.TryGetValue(tenant, out var queue) && queue.TryDequeue(out var next))
                {
                    if (queue.Count == 0)
                    {
                        _pending.Remove(tenant);
                    }

                    // The slot (global and per-tenant) is handed over without being released.
                    current = next;
                    continue;
                }

                var left = _running[tenant] - 1;
                if (left <= 0)
                {
                    _running.Remove(tenant);
                }
                else
                {
                    _running[tenant] = left;
                }

                _global.Release();
                return;
            }
        }
    }

    private async Task DrainAsync()
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

            _pending.Clear();
        }

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
