namespace Autheris.GraphQL.Mcp;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using HotChocolate.Language;
using Microsoft.Extensions.Logging;

/// <summary>
/// MCP view of the catalog GraphQL schema. Names come from <see cref="CatalogSchemaModel"/>, the same model the schema is
/// generated from, so they match the live <c>/graphql</c> API. Reuses the live GraphQL module model and in-memory catalog
/// snapshot where available, keeps the model while the catalog version is unchanged, coalesces cold builds via single-flight,
/// and refreshes in the background without blocking request threads.
/// </summary>
public sealed class CatalogGraphQlMap : IGraphQlCatalogMap, IDisposable, IAsyncDisposable
{
    internal static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultBackstopInterval = TimeSpan.FromMinutes(10);

    private const int MaxSelectionDepth = 32;
    private const int MaxVisitedSelections = 10_000;
    private const int MaxDocumentLength = 64 * 1024;
    private const string RootContext = "<root>";

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly ITableRelationRepository _relationRepository;
    private readonly ICatalogVersionProvider? _versionProvider;
    private readonly ICatalogSnapshotProvider? _snapshotProvider;
    private readonly IEventBus? _eventBus;
    private readonly CatalogGraphQlTypeModule? _typeModule;
    private readonly bool _crossSourceEnabled;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _backstopInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CatalogGraphQlMap>? _logger;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly IDisposable? _eventSubscription;

    private CachedMapState? _state;
    private Task<CachedMapState>? _inFlightBuild;
    private ITimer? _refreshTimer;
    private bool _refreshPending;
    private bool _pendingBypassVersionCache;
    private bool _pendingForceFull;
    private bool _disposed;

    public CatalogGraphQlMap(
        ITableMetadataRepository metadataRepository,
        ITableRelationRepository relationRepository)
        : this(
            metadataRepository,
            relationRepository,
            versionProvider: null,
            snapshotProvider: null,
            eventBus: null,
            typeModule: null,
            crossSourceEnabled: false,
            refreshInterval: TimeSpan.Zero,
            backstopInterval: DefaultBackstopInterval,
            timeProvider: null,
            logger: null)
    {
    }

    public CatalogGraphQlMap(
        ITableMetadataRepository metadataRepository,
        ITableRelationRepository relationRepository,
        ICatalogVersionProvider? versionProvider = null,
        ICatalogSnapshotProvider? snapshotProvider = null,
        IEventBus? eventBus = null,
        CatalogGraphQlTypeModule? typeModule = null,
        bool crossSourceEnabled = false,
        TimeSpan? refreshInterval = null,
        TimeSpan? backstopInterval = null,
        TimeProvider? timeProvider = null,
        ILogger<CatalogGraphQlMap>? logger = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _relationRepository = relationRepository ?? throw new ArgumentNullException(nameof(relationRepository));
        _versionProvider = versionProvider;
        _snapshotProvider = snapshotProvider;
        _eventBus = eventBus;
        _typeModule = typeModule;
        _crossSourceEnabled = crossSourceEnabled;
        _refreshInterval = refreshInterval ?? DefaultRefreshInterval;
        _backstopInterval = backstopInterval ?? DefaultBackstopInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

        // Subscribe last so immediate notifications observe fully initialized dependencies.
        if (_typeModule is not null)
        {
            _typeModule.SchemaModelBuilt += OnTypeModuleSchemaModelBuilt;
        }

        if (_eventBus is not null)
        {
            _eventSubscription = _eventBus.Subscribe<object>(
                ICatalogVersionProvider.CatalogChangedEventChannel,
                _ =>
                {
                    OnCatalogChanged();
                    return Task.CompletedTask;
                });
            _eventBus.ConnectionRestored += OnConnectionRestored;
        }
    }

    private bool EffectiveCrossSourceEnabled => _crossSourceEnabled || (_typeModule?.CrossSourceEnabled == true);

    public Task<GraphQlTableMapping?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<GraphQlTableMapping?>(ct);
        }

        if (Volatile.Read(ref _state) is { } warm)
        {
            return Task.FromResult(warm.GetTableMapping(table));
        }

        return GetTableColdAsync(table, ct);
    }

    public Task<string?> GetRootFieldAsync(TableIdentifier table, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<string?>(ct);
        }

        if (Volatile.Read(ref _state) is { } warm)
        {
            return Task.FromResult(warm.GetRootField(table));
        }

        return GetRootFieldColdAsync(table, ct);
    }

    public Task<TableIdentifier?> ResolveRootFieldAsync(string field, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<TableIdentifier?>(ct);
        }

        if (string.IsNullOrEmpty(field))
        {
            return Task.FromResult<TableIdentifier?>(null);
        }

        if (Volatile.Read(ref _state) is { } warm)
        {
            return Task.FromResult(warm.ResolveRootField(field));
        }

        return ResolveRootFieldColdAsync(field, ct);
    }

    public Task<IReadOnlyList<TableIdentifier>?> ResolveDocumentTablesAsync(string document, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<IReadOnlyList<TableIdentifier>?>(ct);
        }

        // MCP-11: bounded before parsing; an oversized document cannot be resolved, so the guardrail denies the call.
        if (document is null || document.Length > MaxDocumentLength)
        {
            return Task.FromResult<IReadOnlyList<TableIdentifier>?>(null);
        }

        DocumentNode doc;
        try
        {
            doc = Utf8GraphQLParser.Parse(document);
        }
        catch (Exception)
        {
            return Task.FromResult<IReadOnlyList<TableIdentifier>?>(null);
        }

        var operations = doc.Definitions.OfType<OperationDefinitionNode>().ToList();
        if (operations.Count != 1 || operations[0].Operation != OperationType.Query)
        {
            return Task.FromResult<IReadOnlyList<TableIdentifier>?>(null);
        }

        if (Volatile.Read(ref _state) is { } warm)
        {
            return Task.FromResult(ResolveDocumentTablesCore(doc, operations[0], warm.Model));
        }

        return ResolveDocumentTablesColdAsync(doc, operations[0], ct);
    }

    internal async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task<CachedMapState>? active;
            lock (_gate)
            {
                active = _inFlightBuild;
            }

            if (active is null)
            {
                return;
            }

            try
            {
                await active.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Cold build failure clears _inFlightBuild; loop checks whether a follow-up build was scheduled.
            }
        }
    }

    private async Task<GraphQlTableMapping?> GetTableColdAsync(TableIdentifier table, CancellationToken ct)
    {
        var state = await GetOrBuildStateAsync(ct).ConfigureAwait(false);
        return state.GetTableMapping(table);
    }

    private async Task<string?> GetRootFieldColdAsync(TableIdentifier table, CancellationToken ct)
    {
        var state = await GetOrBuildStateAsync(ct).ConfigureAwait(false);
        return state.GetRootField(table);
    }

    private async Task<TableIdentifier?> ResolveRootFieldColdAsync(string field, CancellationToken ct)
    {
        var state = await GetOrBuildStateAsync(ct).ConfigureAwait(false);
        return state.ResolveRootField(field);
    }

    private async Task<IReadOnlyList<TableIdentifier>?> ResolveDocumentTablesColdAsync(
        DocumentNode doc,
        OperationDefinitionNode operation,
        CancellationToken ct)
    {
        var state = await GetOrBuildStateAsync(ct).ConfigureAwait(false);
        return ResolveDocumentTablesCore(doc, operation, state.Model);
    }

    private async Task<CachedMapState> GetOrBuildStateAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        ct.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _state) is { } warm)
        {
            return warm;
        }

        Task<CachedMapState> sharedBuild;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is { } existing)
            {
                return existing;
            }

            sharedBuild = _inFlightBuild ??= StartSharedBuildLocked(bypassVersionCache: false, forceFull: false);
        }

        return await sharedBuild.WaitAsync(ct).ConfigureAwait(false);
    }

    private void OnCatalogChanged() => TriggerBackgroundRefresh(bypassVersionCache: true, forceFull: false);

    private void OnConnectionRestored() => TriggerBackgroundRefresh(bypassVersionCache: true, forceFull: false);

    private void OnTypeModuleSchemaModelBuilt(CatalogSchemaModel model, CatalogVersion? version)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var existing = _state;
            if (existing is not null &&
                existing.Version.HasValue &&
                version.HasValue &&
                !IsAtLeastVersion(version.Value, existing.Version.Value))
            {
                return;
            }

            var next = new CachedMapState(model, version ?? existing?.Version, _timeProvider.GetTimestamp(), transportSeen: null);
            Volatile.Write(ref _state, next);
            EnsureRefreshTimerLocked();
        }
    }

    internal void AdoptModelForTesting(CatalogSchemaModel model, CatalogVersion? version) =>
        OnTypeModuleSchemaModelBuilt(model, version);

    private void TriggerBackgroundRefresh(bool bypassVersionCache, bool forceFull)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_state is null && _inFlightBuild is null)
            {
                // Cold map has not been queried yet; the first caller will read the latest catalog state.
                return;
            }

            if (_inFlightBuild is not null)
            {
                _refreshPending = true;
                _pendingBypassVersionCache |= bypassVersionCache;
                _pendingForceFull |= forceFull;
                return;
            }

            _inFlightBuild = StartSharedBuildLocked(bypassVersionCache, forceFull);
        }
    }

    private Task<CachedMapState> StartSharedBuildLocked(bool bypassVersionCache, bool forceFull)
    {
        var token = _disposeCts.Token;
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(() => ExecuteSharedBuildAsync(bypassVersionCache, forceFull, token), CancellationToken.None);
        }
    }

    private async Task<CachedMapState> ExecuteSharedBuildAsync(bool bypassVersionCache, bool forceFull, CancellationToken ct)
    {
        var existing = Volatile.Read(ref _state);
        try
        {
            ct.ThrowIfCancellationRequested();

            var transportChanged = HasTransportVisibilityChanged(existing);
            var backstopDue = existing is not null
                && _backstopInterval > TimeSpan.Zero
                && _timeProvider.GetElapsedTime(existing.FullBuildTimestamp, _timeProvider.GetTimestamp()) >= _backstopInterval;

            CatalogVersion? targetVersion = _versionProvider is null
                ? null
                : await ReadTargetVersionAsync(bypassVersionCache, ct).ConfigureAwait(false);

            if (existing is not null &&
                !forceFull &&
                !transportChanged &&
                !backstopDue &&
                targetVersion.HasValue &&
                existing.Version.HasValue &&
                targetVersion.Value == existing.Version.Value)
            {
                return existing;
            }

            var next = await BuildNextStateAsync(existing, targetVersion, bypassVersionCache, forceFull, backstopDue, transportChanged, ct)
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (_disposed)
                {
                    return existing ?? next;
                }

                var current = _state;
                var isSameOrNewer = current is null
                    || !current.Version.HasValue
                    || !next.Version.HasValue
                    || IsAtLeastVersion(next.Version.Value, current.Version.Value);
                var canPublish = isSameOrNewer && (
                    current is null
                    || forceFull
                    || transportChanged
                    || backstopDue
                    || !current.Version.HasValue
                    || !next.Version.HasValue
                    || IsAtLeastVersion(next.Version.Value, current.Version.Value));

                if (canPublish)
                {
                    Volatile.Write(ref _state, next);
                    EnsureRefreshTimerLocked();
                    return next;
                }

                return current ?? next;
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
        catch (Exception ex) when (Volatile.Read(ref _state) is { } warm)
        {
            _logger?.LogWarning(ex, "GraphQL catalog map background refresh failed ({FailureType}); serving previous model.", ex.GetType().Name);
            return warm;
        }
        finally
        {
            lock (_gate)
            {
                if (!_disposed && _refreshPending)
                {
                    var nextBypass = _pendingBypassVersionCache;
                    var nextForce = _pendingForceFull;
                    _refreshPending = false;
                    _pendingBypassVersionCache = false;
                    _pendingForceFull = false;
                    _inFlightBuild = StartSharedBuildLocked(nextBypass, nextForce);
                }
                else
                {
                    _inFlightBuild = null;
                }
            }
        }
    }

    private async Task<CachedMapState> BuildNextStateAsync(
        CachedMapState? existing,
        CatalogVersion? targetVersion,
        bool bypassVersionCache,
        bool forceFull,
        bool backstopDue,
        bool transportChanged,
        CancellationToken ct)
    {
        var startedAt = _timeProvider.GetTimestamp();

        // Tier 1: Reuse the live GraphQL schema model when current and consistent with catalog version.
        if (!forceFull &&
            !backstopDue &&
            !transportChanged &&
            _typeModule is not null &&
            _typeModule.TryGetCurrentSchemaModel(out var moduleModel, out var moduleVersion) &&
            (!targetVersion.HasValue || (moduleVersion.HasValue && moduleVersion.Value == targetVersion.Value)))
        {
            if (existing is not null && ReferenceEquals(existing.Model, moduleModel))
            {
                return existing;
            }

            return new CachedMapState(moduleModel, targetVersion ?? moduleVersion, _timeProvider.GetTimestamp(), transportSeen: null);
        }

        // Tier 2: Build purely in memory from CatalogSnapshotProvider when available.
        if (_snapshotProvider is not null)
        {
            try
            {
                if (forceFull ||
                    backstopDue ||
                    (targetVersion.HasValue && _snapshotProvider.IsInitialized &&
                     IsSnapshotBehind(_snapshotProvider.CurrentSnapshot.Version, targetVersion.Value)))
                {
                    await _snapshotProvider.RefreshAsync(ct).ConfigureAwait(false);
                }

                var snapshot = await _snapshotProvider.GetSnapshotAsync(ct).ConfigureAwait(false);
                var localSeen = _typeModule is null ? null : new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var snapshotModel = CatalogSchemaModel.BuildFromSnapshot(
                    snapshot,
                    _logger,
                    dialectRegistry: null,
                    crossSourceEnabled: EffectiveCrossSourceEnabled,
                    tableFilter: _typeModule?.CreateCatalogTableFilter(localSeen),
                    timeProvider: _timeProvider,
                    phase: "mcp-map-snapshot");

                var elapsedMs = (long)_timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
                _logger?.LogDebug(
                    "GraphQL catalog map built from snapshot: tables={TableCount} elapsedMs={ElapsedMs}",
                    snapshotModel.Tables.Count,
                    elapsedMs);

                return new CachedMapState(snapshotModel, snapshot.Version, _timeProvider.GetTimestamp(), localSeen);
            }
            catch (CatalogSnapshotNotReadyException)
            {
                // Fall back to repository load while initial snapshot startup finishes.
            }
        }

        // Tier 3: Repository fallback (standalone test mode, Minimal host without governance repo, or uninitialized snapshot).
        CatalogVersion? versionBefore = targetVersion;
        if (_versionProvider is not null && (bypassVersionCache || !versionBefore.HasValue))
        {
            versionBefore = await ReadTargetVersionAsync(bypassVersionCache: true, ct).ConfigureAwait(false);
        }

        var repoSeen = _typeModule is null ? null : new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var model = await CatalogSchemaModel.BuildAsync(
            _metadataRepository,
            _relationRepository,
            _logger,
            dialectRegistry: null,
            crossSourceEnabled: EffectiveCrossSourceEnabled,
            tableFilter: _typeModule?.CreateCatalogTableFilter(repoSeen),
            fingerprintCache: null,
            epochService: null,
            timeProvider: _timeProvider,
            phase: "mcp-map-repo",
            ct: ct).ConfigureAwait(false);

        var repoElapsedMs = (long)_timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
        _logger?.LogInformation(
            "GraphQL catalog map built from repository: tables={TableCount} elapsedMs={ElapsedMs}",
            model.Tables.Count,
            repoElapsedMs);

        return new CachedMapState(model, versionBefore, _timeProvider.GetTimestamp(), repoSeen);
    }

    private async Task<CatalogVersion?> ReadTargetVersionAsync(bool bypassVersionCache, CancellationToken ct)
    {
        if (_versionProvider is null)
        {
            return null;
        }

        if (bypassVersionCache)
        {
            try
            {
                var direct = await _metadataRepository.GetCatalogVersionAsync(ct).ConfigureAwait(false);
                if (direct.StructureVersion > 0 || direct.ContentVersion > 0)
                {
                    return direct;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Direct catalog version read failed; falling back to version provider.");
            }
        }

        return await _versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
    }

    private bool HasTransportVisibilityChanged(CachedMapState? existing) =>
        (_typeModule?.HasTransportVisibilityChanged() == true) ||
        (existing?.TransportSeen is { IsEmpty: false } seen && _typeModule?.HasTransportVisibilityChanged(seen) == true);

    private static bool IsSnapshotBehind(CatalogVersion snapshotVersion, CatalogVersion targetVersion) =>
        targetVersion.StructureVersion > snapshotVersion.StructureVersion ||
        targetVersion.ContentVersion > snapshotVersion.ContentVersion;

    private static bool IsAtLeastVersion(CatalogVersion candidate, CatalogVersion current) =>
        candidate.StructureVersion >= current.StructureVersion &&
        candidate.ContentVersion >= current.ContentVersion;

    private void EnsureRefreshTimerLocked()
    {
        if (_refreshInterval <= TimeSpan.Zero || _refreshTimer is not null || _disposed)
        {
            return;
        }

        _refreshTimer = _timeProvider.CreateTimer(
            static state => ((CatalogGraphQlMap)state!).TriggerBackgroundRefresh(bypassVersionCache: false, forceFull: false),
            this,
            _refreshInterval,
            _refreshInterval);
    }

    public void Dispose()
    {
        ITimer? timer;
        Task<CachedMapState>? active;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
            _refreshPending = false;
            timer = _refreshTimer;
            _refreshTimer = null;
            active = _inFlightBuild;
        }

        timer?.Dispose();
        if (_typeModule is not null)
        {
            _typeModule.SchemaModelBuilt -= OnTypeModuleSchemaModelBuilt;
        }

        if (_eventBus is not null)
        {
            _eventBus.ConnectionRestored -= OnConnectionRestored;
        }

        _eventSubscription?.Dispose();
        _disposeCts.Cancel();
        if (active is null or { IsCompleted: true })
        {
            _disposeCts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await WaitForIdleAsync().ConfigureAwait(false);
        _disposeCts.Dispose();
    }

    private sealed class CachedMapState(
        CatalogSchemaModel model,
        CatalogVersion? version,
        long fullBuildTimestamp,
        ConcurrentDictionary<string, bool>? transportSeen)
    {
        private readonly ConcurrentDictionary<TableIdentifier, GraphQlTableMapping> _mappings = new();

        public CatalogSchemaModel Model { get; } = model;
        public CatalogVersion? Version { get; } = version;
        public long FullBuildTimestamp { get; } = fullBuildTimestamp;
        public ConcurrentDictionary<string, bool>? TransportSeen { get; } = transportSeen;

        public GraphQlTableMapping? GetTableMapping(TableIdentifier table)
        {
            if (!Model.TablesByIdentifier.TryGetValue(table, out var entry))
            {
                return null;
            }

            return _mappings.GetOrAdd(entry.Identifier, _ => BuildTableMapping(entry));
        }

        public string? GetRootField(TableIdentifier table) =>
            Model.TablesByIdentifier.TryGetValue(table, out var entry) ? entry.QueryFieldName : null;

        public TableIdentifier? ResolveRootField(string field) =>
            Model.TablesByQueryFieldName.TryGetValue(field, out var entry) ? entry.Identifier : null;

        private static GraphQlTableMapping BuildTableMapping(CatalogTableType entry) => new(
            entry.Identifier,
            entry.QueryFieldName,
            entry.TypeName,
            entry.FilterTypeName,
            entry.OrderByTypeName,
            entry.Columns
                .Select(static c => new GraphQlColumnMapping(c.ColumnName, c.FieldName, CatalogGraphQlTypeModule.GetScalarName(c.FieldType)))
                .ToArray(),
            entry.Relations
                .Select(static r => new GraphQlRelationMapping(r.FieldName, r.TargetTableIdentifier, r.IsList, r.ParentColumns, r.ChildColumns))
                .ToArray(),
            CatalogRootFieldSignatures.ListArguments(entry),
            // The schema builder adds the aggregate field whenever aggregation is enabled, which production always does.
            CatalogRootFieldSignatures.Aggregate(entry),
            CatalogRootFieldSignatures.Connection(entry),
            Autheris.Application.DataSources.Services.BulkStreamKey.TryResolve(entry.Metadata)?.Columns ?? []);
    }

    private static IReadOnlyList<TableIdentifier>? ResolveDocumentTablesCore(
        DocumentNode doc,
        OperationDefinitionNode operation,
        CatalogSchemaModel model)
    {
        var fragments = doc.Definitions.OfType<FragmentDefinitionNode>().ToDictionary(f => f.Name.Value, StringComparer.Ordinal);
        var walk = new Walk(model, fragments);
        return CollectRoot(operation.SelectionSet, walk, depth: 0) ? walk.Tables.ToList() : null;
    }

    /// <summary>
    /// State of one document walk (MCP-11). A fragment is expanded once per context (root or one table type): the tables it reaches
    /// are already in <see cref="Tables"/>, so a second spread adds nothing. A fragment that is still being expanded is a cycle.
    /// The number of visited selections is capped, so a document cannot cost more than a fixed amount of work.
    /// </summary>
    private sealed class Walk(CatalogSchemaModel model, Dictionary<string, FragmentDefinitionNode> fragments)
    {
        private readonly HashSet<(string Fragment, string Context)> _done = [];
        private readonly HashSet<(string Fragment, string Context)> _inProgress = [];
        private int _visited;

        public CatalogSchemaModel Model { get; } = model;

        public HashSet<TableIdentifier> Tables { get; } = [];

        /// <summary>Counts a selection; false once the cap is exceeded.</summary>
        public bool Visit() => ++_visited <= MaxVisitedSelections;

        /// <summary>Runs <paramref name="expand"/> once per fragment and context; false for an unknown fragment, a cycle or a failure.</summary>
        public bool Spread(string name, string context, Func<FragmentDefinitionNode, bool> expand)
        {
            if (!fragments.TryGetValue(name, out var fragment))
            {
                return false;
            }

            var key = (name, context);
            if (_done.Contains(key))
            {
                return true;
            }

            if (!_inProgress.Add(key))
            {
                return false;
            }

            var ok = expand(fragment);
            _inProgress.Remove(key);
            if (ok)
            {
                _done.Add(key);
            }

            return ok;
        }
    }

    private static bool CollectRoot(SelectionSetNode set, Walk walk, int depth)
    {
        if (depth > MaxSelectionDepth)
        {
            return false;
        }

        foreach (var selection in set.Selections)
        {
            if (!walk.Visit())
            {
                return false;
            }

            switch (selection)
            {
                case FieldNode field when field.Name.Value.StartsWith("__", StringComparison.Ordinal):
                    // Introspection (__schema, __type, __typename) reads no table.
                    break;
                case FieldNode field:
                    CatalogTableType? table = null;
                    bool isAggregate = false;
                    if (walk.Model.TablesByQueryFieldName.TryGetValue(field.Name.Value, out table))
                    {
                    }
                    else if (AggregateTypeNames.TryGetAggregateRootBase(field.Name.Value, out var aggregateBase) &&
                             walk.Model.TablesByQueryFieldName.TryGetValue(aggregateBase, out table))
                    {
                        isAggregate = true;
                    }
                    else if (field.Name.Value.EndsWith("Connection", StringComparison.Ordinal) &&
                             walk.Model.TablesByQueryFieldName.TryGetValue(field.Name.Value[..^10], out table))
                    {
                        walk.Tables.Add(table.Identifier);
                        if (field.SelectionSet != null && !CollectConnection(field.SelectionSet, table, walk, depth + 1))
                        {
                            return false;
                        }
                        break;
                    }
                    else
                    {
                        return false;
                    }

                    walk.Tables.Add(table.Identifier);
                    if (!isAggregate && field.SelectionSet != null && !CollectTable(field.SelectionSet, table, walk, depth + 1))
                    {
                        return false;
                    }

                    break;
                case InlineFragmentNode inline:
                    if (!CollectRoot(inline.SelectionSet, walk, depth + 1))
                    {
                        return false;
                    }

                    break;
                case FragmentSpreadNode spread:
                    if (!walk.Spread(spread.Name.Value, RootContext, fragment => CollectRoot(fragment.SelectionSet, walk, depth + 1)))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool CollectConnection(SelectionSetNode set, CatalogTableType table, Walk walk, int depth)
    {
        if (depth > MaxSelectionDepth)
        {
            return false;
        }

        foreach (var selection in set.Selections)
        {
            if (!walk.Visit())
            {
                return false;
            }

            switch (selection)
            {
                case FieldNode field when field.Name.Value.StartsWith("__", StringComparison.Ordinal) ||
                                          field.Name.Value is "pageInfo" or "totalCount":
                    break;
                case FieldNode field when field.Name.Value == "edges":
                    if (field.SelectionSet != null && !CollectConnectionEdges(field.SelectionSet, table, walk, depth + 1))
                    {
                        return false;
                    }
                    break;
                case InlineFragmentNode inline:
                    if (!CollectConnection(inline.SelectionSet, table, walk, depth + 1))
                    {
                        return false;
                    }
                    break;
                case FragmentSpreadNode spread:
                    if (!walk.Spread(spread.Name.Value, RootContext, fragment => CollectConnection(fragment.SelectionSet, table, walk, depth + 1)))
                    {
                        return false;
                    }
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool CollectConnectionEdges(SelectionSetNode set, CatalogTableType table, Walk walk, int depth)
    {
        if (depth > MaxSelectionDepth)
        {
            return false;
        }

        foreach (var selection in set.Selections)
        {
            if (!walk.Visit())
            {
                return false;
            }

            switch (selection)
            {
                case FieldNode field when field.Name.Value.StartsWith("__", StringComparison.Ordinal) ||
                                          field.Name.Value is "cursor":
                    break;
                case FieldNode field when field.Name.Value == "node":
                    if (field.SelectionSet != null && !CollectTable(field.SelectionSet, table, walk, depth + 1))
                    {
                        return false;
                    }
                    break;
                case InlineFragmentNode inline:
                    if (!CollectConnectionEdges(inline.SelectionSet, table, walk, depth + 1))
                    {
                        return false;
                    }
                    break;
                case FragmentSpreadNode spread:
                    if (!walk.Spread(spread.Name.Value, RootContext, fragment => CollectConnectionEdges(fragment.SelectionSet, table, walk, depth + 1)))
                    {
                        return false;
                    }
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool CollectTable(SelectionSetNode set, CatalogTableType table, Walk walk, int depth)
    {
        if (depth > MaxSelectionDepth)
        {
            return false;
        }

        foreach (var selection in set.Selections)
        {
            if (!walk.Visit())
            {
                return false;
            }

            switch (selection)
            {
                case FieldNode field:
                    var relation = table.Relations.FirstOrDefault(r => string.Equals(r.FieldName, field.Name.Value, StringComparison.Ordinal));
                    if (relation == null)
                    {
                        // Column or __typename: no further table.
                        break;
                    }

                    if (!walk.Model.TablesByIdentifier.TryGetValue(relation.TargetTableIdentifier, out var target))
                    {
                        return false;
                    }

                    walk.Tables.Add(target.Identifier);
                    if (field.SelectionSet != null && !CollectTable(field.SelectionSet, target, walk, depth + 1))
                    {
                        return false;
                    }

                    break;
                case InlineFragmentNode inline:
                    if (!CollectTable(inline.SelectionSet, table, walk, depth + 1))
                    {
                        return false;
                    }

                    break;
                case FragmentSpreadNode spread:
                    if (!walk.Spread(spread.Name.Value, table.Identifier.ToString(), fragment => CollectTable(fragment.SelectionSet, table, walk, depth + 1)))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }
}
