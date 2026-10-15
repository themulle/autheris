using System.Threading.Channels;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Autheris.Application.Auditing;

/// <summary>
/// Options controlling the bounded group-commit batching sink for high-throughput WORM audit persistence.
/// </summary>
public sealed class AuditGroupCommitOptions
{
    /// <summary>
    /// Maximum number of audit entries flushed in a single database transaction.
    /// </summary>
    public int MaxBatchSize { get; set; } = 64;

    /// <summary>
    /// Maximum coalescing delay in milliseconds when waiting to fill a batch after the first item arrives.
    /// </summary>
    public int MaxBatchDelayMs { get; set; } = 5;

    /// <summary>
    /// Bounded channel capacity for pending audit entries before applying backpressure to callers.
    /// </summary>
    public int ChannelCapacity { get; set; } = 4096;
}

/// <summary>
/// High-throughput group-commit sink for WORM audit events.
/// Coalesces concurrent <see cref="RecordAuditEventAsync"/> calls into bounded batches and persists each batch
/// inside a single database transaction via <see cref="IAuditLogRepository.RecordAuditEventsAsync"/>.
/// Preserves strict fail-closed semantics: callers await durable transaction commit before returning,
/// and any persistence failure faults all callers in the failed batch.
/// </summary>
public sealed class AuditGroupCommitSink : IAsyncDisposable, IDisposable
{
    private readonly IAuditLogRepository _repository;
    private readonly AuditGroupCommitOptions _options;
    private readonly ILogger<AuditGroupCommitSink> _logger;
    private readonly Channel<PendingAuditItem> _channel;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _drainLoopTask;
    private int _disposed;

    public AuditGroupCommitSink(
        IAuditLogRepository repository,
        AuditGroupCommitOptions? options = null,
        ILogger<AuditGroupCommitSink>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _options = options ?? new AuditGroupCommitOptions();
        _logger = logger ?? NullLogger<AuditGroupCommitSink>.Instance;

        int capacity = Math.Max(1, _options.ChannelCapacity);
        _channel = Channel.CreateBounded<PendingAuditItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _drainLoopTask = Task.Run(() => DrainLoopAsync(_shutdownCts.Token));
    }

    /// <summary>
    /// Enqueues an <see cref="AuditLogEntry"/> into the group-commit channel and awaits its durable database commit.
    /// If the batch commit fails, the exception is propagated to the caller (fail-closed).
    /// </summary>
    public async Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new PendingAuditItem(entry, tcs);

        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainLoopAsync(CancellationToken shutdownToken)
    {
        int maxBatchSize = Math.Max(1, _options.MaxBatchSize);
        int maxDelayMs = Math.Max(0, _options.MaxBatchDelayMs);
        var batch = new List<PendingAuditItem>(maxBatchSize);
        var entries = new List<AuditLogEntry>(maxBatchSize);

        try
        {
            while (await _channel.Reader.WaitToReadAsync(shutdownToken).ConfigureAwait(false))
            {
                batch.Clear();
                entries.Clear();

                while (batch.Count < maxBatchSize && _channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                    entries.Add(item.Entry);
                }

                if (batch.Count > 0 && batch.Count < maxBatchSize && maxDelayMs > 0)
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                    delayCts.CancelAfter(maxDelayMs);
                    try
                    {
                        while (batch.Count < maxBatchSize &&
                               await _channel.Reader.WaitToReadAsync(delayCts.Token).ConfigureAwait(false))
                        {
                            while (batch.Count < maxBatchSize && _channel.Reader.TryRead(out var item))
                            {
                                batch.Add(item);
                                entries.Add(item.Entry);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!shutdownToken.IsCancellationRequested)
                    {
                        // Coalescing window elapsed; flush accumulated batch immediately.
                    }
                }

                if (batch.Count > 0)
                {
                    await FlushBatchAsync(batch, entries, shutdownToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // Graceful shutdown requested; drain remaining items below.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in AuditGroupCommitSink drain loop.");
        }
        finally
        {
            // Drain any remaining queued items before exiting so callers never hang.
            batch.Clear();
            entries.Clear();
            while (_channel.Reader.TryRead(out var remaining))
            {
                batch.Add(remaining);
                entries.Add(remaining.Entry);
                if (batch.Count >= maxBatchSize)
                {
                    await FlushBatchAsync(batch, entries, CancellationToken.None).ConfigureAwait(false);
                    batch.Clear();
                    entries.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch, entries, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task FlushBatchAsync(
        List<PendingAuditItem> batch,
        List<AuditLogEntry> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            await _repository.RecordAuditEventsAsync(entries, cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < batch.Count; i++)
            {
                batch[i].Completion.TrySetResult();
            }
        }
        catch (OperationCanceledException oce)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                batch[i].Completion.TrySetCanceled(oce.CancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist audit group-commit batch of {BatchCount} entries (fail-closed).", batch.Count);
            for (int i = 0; i < batch.Count; i++)
            {
                batch[i].Completion.TrySetException(ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        try
        {
            await _drainLoopTask.ConfigureAwait(false);
        }
        finally
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
            _shutdownCts.Dispose();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private readonly record struct PendingAuditItem(AuditLogEntry Entry, TaskCompletionSource Completion);
}
