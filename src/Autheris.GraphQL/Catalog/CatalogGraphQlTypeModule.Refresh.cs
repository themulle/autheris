using Microsoft.Extensions.Logging;

namespace Autheris.GraphQL.Catalog;

// Issue #786: catalog change detection, coalesced rebuild requests, quiet period and refresh timer.
public sealed partial class CatalogGraphQlTypeModule
{
    private const long NoTimestamp = long.MinValue;

    /// <summary>Issue #786: with a version provider, a full candidate check still runs at least this often (a write that missed a version bump).</summary>
    internal static readonly TimeSpan FullCheckBackstopInterval = TimeSpan.FromMinutes(10);

    /// <summary>Issue #786: longest a burst of changes may defer the rebuild after the first deferred change.</summary>
    internal static readonly TimeSpan MaxRebuildDeferral = TimeSpan.FromMinutes(5);

    /// <summary>Issue #786: a rebuild request that was never served (for example a lost event) is raised again after this long.</summary>
    internal static readonly TimeSpan RebuildRequestTimeout = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan SlowCheckLogThreshold = TimeSpan.FromSeconds(5);

    private long _lastFullCheckTimestamp = NoTimestamp;
    private long _deferralStartTimestamp = NoTimestamp;
    private long _rebuildRequestedTimestamp = NoTimestamp;
    private long _buildStartTimestamp = NoTimestamp;
    private int _rebuildRequested;
    private int _coalescedSinceBuild;
    private int _lastCheckSawChange;
    private int _everBuilt;
    private int _lastBuiltTables;
    private int _lastBuiltOmitted;

    internal event Action<CatalogSchemaModel, Autheris.Application.Catalog.CatalogVersion?>? SchemaModelBuilt;

    internal bool CrossSourceEnabled => _crossSourceEnabled;

    internal bool HasTransportVisibilityChanged() => TransportVisibilityChanged();

    internal bool HasTransportVisibilityChanged(System.Collections.Concurrent.ConcurrentDictionary<string, bool> seen) =>
        !seen.IsEmpty && TransportVisibilityChanged(seen);

    internal Func<TableMetadata, bool>? CreateCatalogTableFilter(System.Collections.Concurrent.ConcurrentDictionary<string, bool>? seen = null) =>
        GetTableFilter(seen);

    internal bool TryGetCurrentSchemaModel(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CatalogSchemaModel? model,
        out Autheris.Application.Catalog.CatalogVersion? version)
    {
        var built = _built;
        if (built.Fingerprint is null ||
            built.Model is not { } current ||
            (built.TransportSeen is { } seen ? TransportVisibilityChanged(seen) : TransportVisibilityChanged()))
        {
            model = null;
            version = null;
            return false;
        }

        version = built.Version;
        model = current;
        return true;
    }

    private void NotifySchemaModelBuilt(CatalogSchemaModel schemaModel, Autheris.Application.Catalog.CatalogVersion? version)
    {
        var handler = SchemaModelBuilt;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler.Invoke(schemaModel, version);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "GraphQL catalog schema built listener failed.");
        }
    }

    internal Task<bool> CheckForCatalogChangesAsync(CancellationToken ct = default) =>
        CheckForCatalogChangesAsync(fromTimer: false, forceFull: false, ct);

    private bool ElapsedAtLeast(long since, TimeSpan span) =>
        since != NoTimestamp && _timeProvider.GetElapsedTime(since, _timeProvider.GetTimestamp()) >= span;

    /// <param name="fromTimer">True for the refresh timer: the tick is skipped while a check still runs.</param>
    /// <param name="forceFull">Reads the catalog version from the store instead of the provider's cache (which may predate a write). The unchanged-version early exit still applies to that fresh read.</param>
    /// <param name="ct">Cancels the wait for the check gate and the check.</param>
    internal async Task<bool> CheckForCatalogChangesAsync(bool fromTimer, bool forceFull, CancellationToken ct = default)
    {
        // Serialize instead of dropping: a caller that starts after a completed catalog write always runs its own
        // check and so observes that write. The wait is cancellable; a cancelled wait never took the semaphore.
        // The timer never queues behind a running check (issue #786): that tick is simply skipped.
        if (fromTimer)
        {
            if (!await _checkGate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return false;
            }
        }
        else
        {
            await _checkGate.WaitAsync(ct).ConfigureAwait(false);
        }

        var changed = false;
        var raise = false;
        var forceRaise = false;
        var checkStart = _timeProvider.GetTimestamp();
        try
        {
            var builtStruct = Volatile.Read(ref _builtStructureVersion);
            var builtContent = Volatile.Read(ref _builtContentVersion);
            var transportChanged = TransportVisibilityChanged();
            if (transportChanged)
            {
                _tableFingerprintCache.Clear();
                _tableRecipeCache.Clear();
                Volatile.Write(ref _pendingBuild, null);
                Volatile.Write(ref _deferralStartTimestamp, NoTimestamp);
            }

            Autheris.Application.Catalog.CatalogVersion? currentVer = null;
            if (_versionProvider != null)
            {
                try
                {
                    // Issue #802: a forced check reads the store directly; the provider's cache may predate the write.
                    currentVer = forceFull
                        ? await _metadataRepo.GetCatalogVersionAsync(ct).ConfigureAwait(false)
                        : await _versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
                    if (!transportChanged &&
                        builtStruct >= 0 &&
                        currentVer.Value.StructureVersion == builtStruct &&
                        currentVer.Value.ContentVersion == builtContent &&
                        Volatile.Read(ref _deferralStartTimestamp) == NoTimestamp &&
                        _lastFullCheckTimestamp != NoTimestamp &&
                        !ElapsedAtLeast(_lastFullCheckTimestamp, FullCheckBackstopInterval))
                    {
                        Volatile.Write(ref _lastCheckSawChange, 0);
                        return false;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "GraphQL catalog structure version check failed; falling back to schema fingerprint check.");
                }
            }

            var candidateSeen = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            CatalogSchemaModel candidateModel;
            string current;
            try
            {
                candidateModel = await CatalogSchemaModel.BuildAsync(
                    _metadataRepo,
                    _relationRepo,
                    _logger,
                    dialectRegistry: null,
                    crossSourceEnabled: _crossSourceEnabled,
                    tableFilter: GetTableFilter(candidateSeen),
                    fingerprintCache: _tableFingerprintCache,
                    epochService: _epochService,
                    timeProvider: _timeProvider,
                    phase: "check",
                    ct: ct).ConfigureAwait(false);
                current = candidateModel.Fingerprint();
                _lastFullCheckTimestamp = _timeProvider.GetTimestamp();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "GraphQL catalog change check failed; retrying with the next interval.");
                return false;
            }

            var built = _built;
            if (!transportChanged && string.Equals(current, built.Fingerprint, StringComparison.Ordinal))
            {
                if (currentVer.HasValue)
                {
                    // Never lowered (issue #802): a cached provider version may be older than the store read of a forced check.
                    RaiseToAtLeast(ref _builtStructureVersion, currentVer.Value.StructureVersion);
                    RaiseToAtLeast(ref _builtContentVersion, currentVer.Value.ContentVersion);
                    var raised = built.Version is { } prevVer
                        ? new Autheris.Application.Catalog.CatalogVersion(
                            Math.Max(prevVer.StructureVersion, currentVer.Value.StructureVersion),
                            Math.Max(prevVer.ContentVersion, currentVer.Value.ContentVersion))
                        : currentVer.Value;
                    if (built.Version != raised)
                    {
                        _built = built with { Version = raised };
                    }
                }
                Volatile.Write(ref _pendingBuild, null);
                Volatile.Write(ref _deferralStartTimestamp, NoTimestamp);
                Volatile.Write(ref _lastCheckSawChange, 0);
                return false;
            }

            var previous = Volatile.Read(ref _pendingBuild);
            var furtherChange = previous is null || !string.Equals(previous.Fingerprint, current, StringComparison.Ordinal);
            Volatile.Write(ref _pendingBuild, new PendingBuild(candidateModel, current, candidateSeen, currentVer));
            changed = true;

            // Quiet period (issue #786): the first change after a quiet check rebuilds at once (leading edge); further
            // changes of a burst wait until one check sees nothing new, or until MaxRebuildDeferral has passed. The newest
            // model always wins, so the trailing rebuild never serves an older catalog.
            var deferralStart = Volatile.Read(ref _deferralStartTimestamp);
            if (transportChanged)
            {
                forceRaise = true;
                raise = true;
            }
            else if (DropsBuiltSchemaElements(_currentSchemaModel, candidateModel))
            {
                // Issue #835: a hide, deny or retire must not wait for the quiet period or the deferral cap. The request still
                // goes through the single-outstanding latch, so a burst of restrictive changes cannot storm the rebuild.
                raise = true;
            }
            else if (deferralStart == NoTimestamp && Volatile.Read(ref _lastCheckSawChange) == 0)
            {
                raise = true;
            }
            else if (deferralStart != NoTimestamp && !furtherChange)
            {
                raise = true;
            }
            else
            {
                if (deferralStart == NoTimestamp)
                {
                    Volatile.Write(ref _deferralStartTimestamp, checkStart);
                }
                else if (ElapsedAtLeast(deferralStart, MaxRebuildDeferral))
                {
                    raise = true;
                }
            }

            Volatile.Write(ref _lastCheckSawChange, furtherChange ? 1 : 0);
            if (raise)
            {
                Volatile.Write(ref _deferralStartTimestamp, NoTimestamp);
            }
        }
        finally
        {
            _checkGate.Release();
            var elapsed = _timeProvider.GetElapsedTime(checkStart, _timeProvider.GetTimestamp());
            if (elapsed >= SlowCheckLogThreshold)
            {
                _logger?.LogInformation("GraphQL catalog check finished: changed={Changed} elapsedMs={ElapsedMs} phase=check", changed, (long)elapsed.TotalMilliseconds);
            }
            else
            {
                _logger?.LogDebug("GraphQL catalog check finished: changed={Changed} elapsedMs={ElapsedMs} phase=check", changed, (long)elapsed.TotalMilliseconds);
            }
        }

        // Raised outside the semaphore so a re-entrant TypesChanged handler cannot deadlock on it.
        if (changed)
        {
            if (raise)
            {
                RequestRebuild(forceRaise);
            }
            else
            {
                _logger?.LogDebug("GraphQL catalog changed again; rebuild deferred until the changes settle.");
                ScheduleFollowUpCheck();
            }
        }

        return changed;
    }

    /// <summary>
    /// Issue #835: true when the candidate no longer has a root field, type, column or relation field that the served schema
    /// has. Additive changes are not restrictive; they may wait for the quiet period.
    /// </summary>
    internal static bool DropsBuiltSchemaElements(CatalogSchemaModel? built, CatalogSchemaModel candidate)
    {
        if (built is null)
        {
            return false;
        }

        foreach (var table in built.Tables)
        {
            if (!candidate.TablesByIdentifier.TryGetValue(table.Identifier, out var next) ||
                !string.Equals(next.QueryFieldName, table.QueryFieldName, StringComparison.Ordinal) ||
                !string.Equals(next.TypeName, table.TypeName, StringComparison.Ordinal))
            {
                return true;
            }

            var columns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in next.Columns)
            {
                columns.Add(c.FieldName);
            }

            foreach (var c in table.Columns)
            {
                if (!columns.Contains(c.FieldName))
                {
                    return true;
                }
            }

            var relations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in next.Relations)
            {
                relations.Add(r.FieldName);
            }

            foreach (var r in table.Relations)
            {
                if (!relations.Contains(r.FieldName))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Issue #786: at most one rebuild request is outstanding. A request is outstanding from the moment it is raised until
    /// <see cref="CreateTypesAsync"/> starts; later changes only replace the pending model, which that build then takes.
    /// </summary>
    private void RequestRebuild(bool force)
    {
        var handler = TypesChanged;
        if (handler is null)
        {
            // Nobody listens yet: nothing is latched and the next check counts as a leading edge again.
            Volatile.Write(ref _lastCheckSawChange, 0);
            return;
        }

        if (Interlocked.CompareExchange(ref _rebuildRequested, 1, 0) != 0)
        {
            var stale = ElapsedAtLeast(Volatile.Read(ref _rebuildRequestedTimestamp), RebuildRequestTimeout);
            if (!stale && !force)
            {
                Interlocked.Increment(ref _coalescedSinceBuild);
                _logger?.LogDebug("GraphQL catalog changed; rebuild request coalesced with the one already queued.");
                return;
            }
        }

        Volatile.Write(ref _rebuildRequestedTimestamp, _timeProvider.GetTimestamp());
        _logger?.LogInformation("GraphQL catalog changed; rebuilding the GraphQL schema.");
        try
        {
            handler.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            Interlocked.Exchange(ref _rebuildRequested, 0);
            throw;
        }
    }

    private void EnsureRefreshTimer()
    {
        if (_refreshInterval <= TimeSpan.Zero || _refreshTimer != null)
        {
            return;
        }

        lock (_timerLock)
        {
            // Non-reentrant (issue #786): a tick that finds a check still running is skipped inside the check.
            if (_refreshTimer is null)
            {
                RecordNextTick();
            }

            _refreshTimer ??= _timeProvider.CreateTimer(
                _ => _ = RunTimerCheckAsync(),
                null,
                _refreshInterval,
                _refreshInterval);
        }
    }

    private async Task RunTimerCheckAsync()
    {
        RecordNextTick();
        try
        {
            await CheckForCatalogChangesAsync(fromTimer: true, forceFull: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "GraphQL catalog change check failed; retrying with the next interval.");
        }
    }

    /// <summary>Called by the executor diagnostics when Hot Chocolate finished creating an executor from this module's types.</summary>
    internal void ReportExecutorCreated()
    {
        var start = Volatile.Read(ref _buildStartTimestamp);
        if (start == NoTimestamp)
        {
            Interlocked.Exchange(ref _buildInFlight, 0);
            return;
        }

        var elapsed = _timeProvider.GetElapsedTime(start, _timeProvider.GetTimestamp());
        Volatile.Write(ref _lastBuildDurationTicks, elapsed.Ticks);
        Interlocked.Exchange(ref _buildInFlight, 0);
        var elapsedMs = (long)elapsed.TotalMilliseconds;
        _logger?.LogInformation(
            "GraphQL catalog schema build finished: tables={Tables} omitted={Omitted} elapsedMs={ElapsedMs} phase=rebuild",
            Volatile.Read(ref _lastBuiltTables),
            Volatile.Read(ref _lastBuiltOmitted),
            elapsedMs);
    }

    private async ValueTask<(CatalogSchemaModel Model, Autheris.Application.Catalog.CatalogVersion? Version)> BuildFreshSchemaModelAsync(
        CancellationToken cancellationToken)
    {
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        Autheris.Application.Catalog.CatalogSnapshot? snapshot = null;
        if (_snapshotProvider is not null)
        {
            try
            {
                snapshot = await _snapshotProvider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Autheris.Domain.Exceptions.CatalogSnapshotNotReadyException)
            {
                snapshot = null;
            }
        }

        Autheris.Application.Catalog.CatalogVersion? catVer;
        CatalogSchemaModel schemaModel;
        if (snapshot is not null)
        {
            catVer = snapshot.Version;
            schemaModel = await CatalogSchemaModel.BuildFromSnapshotAsync(
                snapshot,
                _logger,
                dialectRegistry: null,
                crossSourceEnabled: _crossSourceEnabled,
                tableFilter: GetTableFilter(seen),
                fingerprintCache: _tableFingerprintCache,
                epochService: _epochService,
                timeProvider: _timeProvider,
                phase: "rebuild",
                ct: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            catVer = _versionProvider is null
                ? null
                : await _versionProvider.GetCatalogVersionAsync(cancellationToken).ConfigureAwait(false);
            schemaModel = await CatalogSchemaModel.BuildAsync(
                _metadataRepo,
                _relationRepo,
                _logger,
                dialectRegistry: null,
                crossSourceEnabled: _crossSourceEnabled,
                tableFilter: GetTableFilter(seen),
                fingerprintCache: _tableFingerprintCache,
                epochService: _epochService,
                timeProvider: _timeProvider,
                phase: "rebuild",
                ct: cancellationToken).ConfigureAwait(false);
        }

        _lastFullCheckTimestamp = _timeProvider.GetTimestamp();
        _transportSeen = seen;
        _currentSchemaModel = schemaModel;
        CurrentLookup = GetTableMetadataForField;
        if (catVer.HasValue)
        {
            Volatile.Write(ref _builtStructureVersion, catVer.Value.StructureVersion);
            Volatile.Write(ref _builtContentVersion, catVer.Value.ContentVersion);
        }

        _built = new BuiltState(schemaModel.Fingerprint(), schemaModel.OmittedTables, schemaModel, catVer, seen);
        return (schemaModel, catVer);
    }
}
