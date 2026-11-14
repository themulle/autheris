namespace Autheris.Application.Catalog;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Ambient access to the active <see cref="ICatalogWriteScope"/>. Used by catalog repositories only; a write is deferred
/// solely while the scope is active, not disposed and bound to the same flusher (a disposed or foreign scope never defers).
/// </summary>
public static class CatalogWriteScopeContext
{
    internal static readonly AsyncLocal<CatalogWriteScope?> Slot = new();

    /// <summary>The active scope bound to <paramref name="flusher"/>, or null (no scope, disposed scope, foreign scope).</summary>
    public static CatalogWriteScope? TryGetActive(ICatalogVersionFlusher flusher)
    {
        var scope = Slot.Value;
        return scope is not null && scope.IsActiveFor(flusher) ? scope : null;
    }
}

/// <summary>
/// Outermost catalog write scope: accumulates the version deltas of additive writes and flushes them once.
/// </summary>
public sealed class CatalogWriteScope : ICatalogWriteScope
{
    private readonly ICatalogVersionFlusher _flusher;
    private readonly ICatalogDirtyFlag _dirtyFlag;
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private bool _pendingStructure;
    private bool _pendingContent;
    private int _pendingTables;
    private int _flushedChunks;
    private bool _disposed;

    internal CatalogWriteScope(
        string writer,
        ICatalogVersionFlusher flusher,
        ICatalogDirtyFlag dirtyFlag,
        ILogger? logger,
        TimeProvider timeProvider)
    {
        Writer = writer;
        _flusher = flusher;
        _dirtyFlag = dirtyFlag;
        _logger = logger;
        _timeProvider = timeProvider;
        ScopeId = Guid.NewGuid();
    }

    public Guid ScopeId { get; }

    public string Writer { get; }

    /// <summary>Number of version flushes this scope has performed (diagnostics and tests).</summary>
    public int FlushCount => Volatile.Read(ref _flushedChunks);

    internal bool IsActiveFor(ICatalogVersionFlusher flusher)
    {
        lock (_gate)
        {
            return !_disposed && ReferenceEquals(_flusher, flusher);
        }
    }

    /// <summary>
    /// Records a committed additive table write. Flushes one version bump when the chunk size is reached, or immediately
    /// when the scope was disposed in the meantime. Never throws for flush problems (the dirty flag repairs them).
    /// </summary>
    public async Task RecordAdditiveWriteAsync(long structureDelta, long contentDelta, CancellationToken ct = default)
    {
        long flushStructure = 0, flushContent = 0;
        lock (_gate)
        {
            _pendingStructure |= structureDelta > 0;
            _pendingContent |= contentDelta > 0;
            _pendingTables++;
            if (_disposed || _pendingTables >= CatalogWriteScopeSettings.ChunkSize)
            {
                (flushStructure, flushContent) = TakePendingLocked();
            }
        }

        if (flushStructure + flushContent > 0)
        {
            await FlushAsync(flushStructure, flushContent).ConfigureAwait(false);
        }
    }

    /// <summary>Flushes everything pending now (called after a restrictive write committed inside the scope).</summary>
    public async Task FlushPendingAsync(CancellationToken ct = default)
    {
        long flushStructure, flushContent;
        lock (_gate)
        {
            (flushStructure, flushContent) = TakePendingLocked();
        }

        if (flushStructure + flushContent > 0)
        {
            await FlushAsync(flushStructure, flushContent).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        long flushStructure, flushContent;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            (flushStructure, flushContent) = TakePendingLocked();
        }

        if (ReferenceEquals(CatalogWriteScopeContext.Slot.Value, this))
        {
            CatalogWriteScopeContext.Slot.Value = null;
        }

        if (flushStructure + flushContent > 0)
        {
            await FlushAsync(flushStructure, flushContent).ConfigureAwait(false);
        }
    }

    internal ICatalogWriteScope Join() => new JoinedScope(this);

    private (long Structure, long Content) TakePendingLocked()
    {
        var result = (_pendingStructure ? 1L : 0L, _pendingContent ? 1L : 0L);
        _pendingStructure = false;
        _pendingContent = false;
        _pendingTables = 0;
        return result;
    }

    private async Task FlushAsync(long structureDelta, long contentDelta)
    {
        // A flush has its own bounded token: never the writer's (possibly cancelled) token and never CancellationToken.None.
        using var cts = new CancellationTokenSource(CatalogWriteScopeSettings.FlushTimeout, _timeProvider);
        for (var attempt = 1; attempt <= CatalogWriteScopeSettings.MaxFlushAttempts; attempt++)
        {
            try
            {
                await _flusher.FlushCatalogVersionAsync(structureDelta, contentDelta, cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _flushedChunks);
                return;
            }
            catch (Exception) when (attempt < CatalogWriteScopeSettings.MaxFlushAttempts && !cts.IsCancellationRequested)
            {
                // retry within the same time budget
            }
            catch (Exception)
            {
                break;
            }
        }

        // The writes are committed; only the version bump is missing. The snapshot poll forces it.
        _dirtyFlag.MarkDirty();
        _logger?.LogCritical(
            "Catalog write scope could not flush the catalog version (writer {Writer}, structure delta {StructureDelta}, content delta {ContentDelta}); the catalog snapshot poll will repair it.",
            Writer,
            structureDelta,
            contentDelta);
    }

    private sealed class JoinedScope(CatalogWriteScope outer) : ICatalogWriteScope
    {
        public Guid ScopeId => outer.ScopeId;

        public string Writer => outer.Writer;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
