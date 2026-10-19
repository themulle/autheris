namespace Autheris.Application.Streaming.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>A tail subscriber cannot continue without losing events (buffer overflow, expired resume window, catch-up limit).</summary>
public sealed class CdcResyncRequiredException : Exception
{
    public const string Code = "CDC_RESYNC_REQUIRED";

    public CdcResyncRequiredException(long lastDeliveredOffset)
        : base(Code)
    {
        LastDeliveredOffset = lastDeliveredOffset;
    }

    public CdcResyncRequiredException()
        : this(0)
    {
    }

    public CdcResyncRequiredException(string message)
        : base(message)
    {
    }

    public CdcResyncRequiredException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public long LastDeliveredOffset { get; }
}

/// <summary>
/// Node-local shared tail reader (PLAN-CDC-99 D-A13, D-B7): one reader per stream with at least one local listener, started at the
/// head, decrypting each event once and fanning the immutable <see cref="CdcStreamDelivery"/> out to bounded per-subscriber buffers.
/// A full buffer completes that subscriber with <see cref="CdcResyncRequiredException"/>; it never blocks the reader or other
/// subscribers. Readers wake on <see cref="ICdcStreamSignal"/> and also poll, so a lost signal only adds latency. An event that cannot
/// be decrypted is never skipped (fail closed); the reader retries until retention moves the tail past it, which is reported as a gap.
/// </summary>
public sealed class CdcStreamTailHub : ICdcStreamTailHub, IAsyncDisposable
{
    private const int ReadPageSize = 100;

    private readonly ICdcStreamStore _store;
    private readonly ICdcStreamCrypto _crypto;
    private readonly ICdcStreamSignal _signal;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CdcStreamTailHub> _logger;
    private readonly ConcurrentDictionary<string, StreamReader> _readers = new(StringComparer.Ordinal);
    private readonly object _signalGate = new();
    private IAsyncDisposable? _signalSubscription;
    private int _activeCatchUps;
    private int _disposed;

    public CdcStreamTailHub(
        ICdcStreamStore store, ICdcStreamCrypto crypto, ICdcStreamSignal signal, IOptionsMonitor<GatewayOptions> options,
        ILogger<CdcStreamTailHub> logger, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    private CdcStreamOptions Opt => _options.CurrentValue.CdcStream;

    /// <summary>Number of streams with a running local reader (diagnostics and tests).</summary>
    public int ActiveReaders => _readers.Count;

    public async IAsyncEnumerable<CdcStreamDelivery> AttachAsync(CdcStreamId id, long? fromOffset, [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        EnsureSignalSubscription();

        var descriptor = await _store.GetStreamAsync(id, ct).ConfigureAwait(false);
        if (descriptor is null)
        {
            yield break;
        }

        if (fromOffset is { } requested && requested > descriptor.HeadOffset + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fromOffset), "The requested offset is beyond the stream head.");
        }

        var buffer = Channel.CreateBounded<CdcStreamDelivery>(new BoundedChannelOptions(Math.Max(1, Opt.SubscriberBufferSize))
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var subscriber = new Subscriber(buffer);

        StreamReader reader;
        long joinPoint;
        while (true)
        {
            reader = _readers.GetOrAdd(id.Value, _ => new StreamReader(this, id, descriptor.Table, descriptor.HeadOffset));
            if (reader.TryAdd(subscriber, out joinPoint))
            {
                break;
            }

            _readers.TryRemove(new KeyValuePair<string, StreamReader>(id.Value, reader));
        }

        try
        {
            if (fromOffset is { } from && from <= joinPoint)
            {
                await foreach (var catchUp in CatchUpAsync(id, descriptor.Table, from, joinPoint, subscriber, ct).ConfigureAwait(false))
                {
                    yield return catchUp;
                }
            }

            while (await buffer.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (buffer.Reader.TryRead(out var delivery))
                {
                    subscriber.LastOffset = delivery.Record.Offset;
                    yield return delivery;
                }
            }

            // Completed by the hub: surface an overflow instead of ending silently.
            if (subscriber.Overflow)
            {
                throw new CdcResyncRequiredException(subscriber.LastOffset);
            }
        }
        finally
        {
            if (reader.RemoveAndStopIfEmpty(subscriber))
            {
                _readers.TryRemove(new KeyValuePair<string, StreamReader>(id.Value, reader));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var reader in _readers.Values)
        {
            reader.Stop();
        }

        _readers.Clear();
        IAsyncDisposable? sub;
        lock (_signalGate)
        {
            sub = _signalSubscription;
            _signalSubscription = null;
        }

        if (sub is not null)
        {
            await sub.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void EnsureSignalSubscription()
    {
        lock (_signalGate)
        {
            _signalSubscription ??= _signal.Subscribe((id, _) =>
            {
                if (_readers.TryGetValue(id.Value, out var reader))
                {
                    reader.Wake();
                }

                return ValueTask.CompletedTask;
            });
        }
    }

    private async IAsyncEnumerable<CdcStreamDelivery> CatchUpAsync(
        CdcStreamId id, Autheris.Domain.Common.TableIdentifier table, long from, long joinPoint, Subscriber subscriber,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // SEC-CDC99-17: bounded number of concurrent catch-up readers per node.
        if (Interlocked.Increment(ref _activeCatchUps) > Math.Max(1, Opt.MaxConcurrentCatchUps))
        {
            Interlocked.Decrement(ref _activeCatchUps);
            throw new CdcResyncRequiredException(from - 1);
        }

        try
        {
            var next = from;
            var window = TimeSpan.FromSeconds(Math.Max(0, Opt.GraphQlResumeWindowSeconds));
            while (next <= joinPoint)
            {
                var result = await _store.ReadAsync(id, next, ReadPageSize, ct).ConfigureAwait(false);
                if (result.TailOffset > next)
                {
                    // Retention removed the requested range: an explicit gap, never silent (SEC-CDC99-18).
                    yield return GapDelivery(id, table, next, result.TailOffset);
                    next = result.TailOffset;
                    continue;
                }

                if (result.Events.Count == 0)
                {
                    yield break;
                }

                foreach (var record in result.Events)
                {
                    if (record.Offset > joinPoint)
                    {
                        yield break;
                    }

                    // SEC-CDC99-02: only events within the resume window may be replayed.
                    if (_time.GetUtcNow() - record.ProducedAt > window)
                    {
                        throw new CdcResyncRequiredException(record.Offset - 1);
                    }

                    var delivery = await DecodeAsync(record, table, ct).ConfigureAwait(false);
                    subscriber.LastOffset = record.Offset;
                    next = record.Offset + 1;
                    yield return delivery;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeCatchUps);
        }
    }

    internal async ValueTask<CdcStreamDelivery> DecodeAsync(CdcStreamEventRecord record, Autheris.Domain.Common.TableIdentifier table, CancellationToken ct)
    {
        var plaintext = await _crypto.UnprotectEventAsync(record, ct).ConfigureAwait(false);
        try
        {
            return record.Kind == CdcStreamEventKind.Change
                ? new CdcStreamDelivery(record, CdcPayloadCodec.DecodeChange(plaintext), null, table)
                : new CdcStreamDelivery(record, null, CdcPayloadCodec.DecodeControl(plaintext), table);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Synthesised gap delivery: <see cref="CdcControlPayload.StoredVersion"/> carries the requested offset and
    /// <see cref="CdcControlPayload.MinValidVersion"/> the resume offset (the first retained offset); there is no row data.
    /// </summary>
    internal static CdcStreamDelivery GapDelivery(CdcStreamId id, Autheris.Domain.Common.TableIdentifier table, long fromOffset, long resumeOffset) =>
        new(
            new CdcStreamEventRecord(id, resumeOffset - 1, 0, CdcStreamEventKind.Gap, null, CdcTenantScope.Unresolved, null, null,
                new CdcProtectedBlob(string.Empty, []), DateTimeOffset.MinValue),
            null,
            new CdcControlPayload("retention", fromOffset, resumeOffset, null),
            table);

    private sealed class Subscriber(Channel<CdcStreamDelivery> buffer)
    {
        public Channel<CdcStreamDelivery> Buffer { get; } = buffer;

        public long LastOffset;

        public volatile bool Overflow;
    }

    private sealed class StreamReader : IDisposable
    {
        private readonly CdcStreamTailHub _hub;
        private readonly CdcStreamId _id;
        private readonly Autheris.Domain.Common.TableIdentifier _table;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();
        private readonly List<Subscriber> _subscribers = [];
        private TaskCompletionSource _wake = NewWake();
        private bool _pendingWake;
        private long _position;
        private bool _stopped;
        private Task? _loop;

        public StreamReader(CdcStreamTailHub hub, CdcStreamId id, Autheris.Domain.Common.TableIdentifier table, long startHead)
        {
            _hub = hub;
            _id = id;
            _table = table;
            _position = startHead;
        }

        /// <summary>Atomically removes the subscriber and stops the reader when it was the last one (no lost-subscriber race).</summary>
        public bool RemoveAndStopIfEmpty(Subscriber subscriber)
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriber);
                if (_subscribers.Count != 0)
                {
                    return false;
                }

                _stopped = true;
            }

            CancelQuietly();
            return true;
        }

        public bool TryAdd(Subscriber subscriber, out long joinPoint)
        {
            lock (_gate)
            {
                joinPoint = _position;
                if (_stopped)
                {
                    return false;
                }

                _subscribers.Add(subscriber);
                _loop ??= Task.Run(() => RunAsync(_cts.Token));
                return true;
            }
        }

        public void Remove(Subscriber subscriber)
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriber);
            }
        }

        public void Wake()
        {
            TaskCompletionSource tcs;
            lock (_gate)
            {
                _pendingWake = true;
                tcs = _wake;
            }

            tcs.TrySetResult();
        }

        public void Stop()
        {
            lock (_gate)
            {
                _stopped = true;
            }

            CancelQuietly();
        }

        public void Dispose() => _cts.Dispose();

        private void CancelQuietly()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The loop already ended and released the source.
            }
        }

        private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task RunAsync(CancellationToken ct)
        {
            var failureLogged = false;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var progressed = false;
                    try
                    {
                        progressed = await PumpOnceAsync(ct).ConfigureAwait(false);
                        failureLogged = false;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (!failureLogged)
                        {
                            _hub._logger.LogWarning("CDC tail reader of stream {StreamId} is waiting: {Code}.", _id.Value, ex is CdcStreamKeyUnavailableException ? ex.Message : ex.GetType().Name);
                            failureLogged = true;
                        }
                    }

                    if (progressed)
                    {
                        continue;
                    }

                    TaskCompletionSource tcs;
                    lock (_gate)
                    {
                        // A wake-up that arrived while pumping is consumed here instead of being lost.
                        if (_pendingWake)
                        {
                            _pendingWake = false;
                            continue;
                        }

                        tcs = _wake = NewWake();
                    }

                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var delay = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, _hub.Opt.TailPollIntervalMilliseconds)), _hub._time, delayCts.Token);
                    await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
                    await delayCts.CancelAsync().ConfigureAwait(false);
                    lock (_gate)
                    {
                        _pendingWake = false;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
            finally
            {
                CompleteAll();
                Dispose();
            }
        }

        private async Task<bool> PumpOnceAsync(CancellationToken ct)
        {
            var from = _position + 1;
            var result = await _hub._store.ReadAsync(_id, from, ReadPageSize, ct).ConfigureAwait(false);
            if (result.TailOffset > from)
            {
                Publish(GapDelivery(_id, _table, from, result.TailOffset), result.TailOffset - 1);
                return true;
            }

            if (result.Events.Count == 0)
            {
                return false;
            }

            foreach (var record in result.Events)
            {
                if (record.Offset != _position + 1)
                {
                    return false;
                }

                var delivery = await _hub.DecodeAsync(record, _table, ct).ConfigureAwait(false);
                Publish(delivery, record.Offset);
            }

            return true;
        }

        private void Publish(CdcStreamDelivery delivery, long newPosition)
        {
            List<Subscriber> snapshot;
            lock (_gate)
            {
                _position = newPosition;
                snapshot = [.. _subscribers];
            }

            foreach (var subscriber in snapshot)
            {
                if (!subscriber.Buffer.Writer.TryWrite(delivery))
                {
                    subscriber.Overflow = true;
                    subscriber.Buffer.Writer.TryComplete();
                    Remove(subscriber);
                }
            }
        }

        private void CompleteAll()
        {
            List<Subscriber> snapshot;
            lock (_gate)
            {
                snapshot = [.. _subscribers];
                _subscribers.Clear();
            }

            foreach (var subscriber in snapshot)
            {
                subscriber.Buffer.Writer.TryComplete();
            }
        }
    }
}
