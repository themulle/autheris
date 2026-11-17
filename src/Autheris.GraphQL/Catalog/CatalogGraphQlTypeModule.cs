using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Analytics;
using Autheris.Application.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution.Configuration;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.GraphQL.Catalog;

public sealed class CatalogGraphQlTypeModule : ITypeModule, IDisposable, ICatalogSchemaDiagnostics
{
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly ITableRelationRepository _relationRepo;
    private readonly Autheris.Application.Catalog.ICatalogVersionProvider? _versionProvider;
    private readonly Microsoft.Extensions.Logging.ILogger<CatalogGraphQlTypeModule>? _logger;
    private readonly TimeSpan _refreshInterval;
    private readonly bool _aggregationEnabled;
    private readonly bool _crossSourceEnabled;
    private readonly bool _metricsEnabled;
    private readonly object _timerLock = new();
    private ITimer? _refreshTimer;
    private sealed record BuiltState(string? Fingerprint, IReadOnlyList<CatalogOmittedTable> OmittedTables);
    private sealed record PendingBuild(
        CatalogSchemaModel Model,
        string Fingerprint,
        System.Collections.Concurrent.ConcurrentDictionary<string, bool> TransportSeen,
        Autheris.Application.Catalog.CatalogVersion? CatalogVersion);

    // One volatile reference so readers never observe a fingerprint paired with another build's omissions.
    private volatile BuiltState _built = new(null, []);
    private volatile CatalogSchemaModel? _currentSchemaModel;
    private PendingBuild? _pendingBuild;
    private long _builtStructureVersion = -1;
    private long _builtContentVersion = -1;
    private readonly SemaphoreSlim _checkGate = new(1, 1);

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
    private readonly CatalogTableFingerprintCache _tableFingerprintCache = new();
    internal CatalogTableFingerprintCache FingerprintCache => _tableFingerprintCache;

    /// <summary>
    /// Issue #421: Ambient table metadata lookup for GraphQL cost analysis.
    /// </summary>
    public static Func<string, TableMetadata?>? CurrentLookup { get; set; }

    public TableMetadata? GetTableMetadataForField(string fieldName)
    {
        var model = _currentSchemaModel;
        if (model == null) return null;
        if (model.TablesByQueryFieldName.TryGetValue(fieldName, out var table))
            return table.Metadata;
        if (fieldName.EndsWith("Connection", StringComparison.Ordinal) &&
            model.TablesByQueryFieldName.TryGetValue(fieldName[..^10], out var connTable))
            return connTable.Metadata;
        return null;
    }

    private readonly GraphQLOptions _graphQlOptions;
    private readonly Autheris.Application.DataSources.IDataSourceOptionsResolver? _transportResolver;
    private readonly IEpochValidationService? _epochService;
    private readonly TimeProvider _timeProvider;

    /// <summary>Logical source -> was it visible over GraphQL when the schema was built (issue #447).</summary>
    private System.Collections.Concurrent.ConcurrentDictionary<string, bool> _transportSeen = new(StringComparer.OrdinalIgnoreCase);

    public CatalogGraphQlTypeModule(
        ITableMetadataRepository metadataRepo,
        ITableRelationRepository relationRepo,
        Microsoft.Extensions.Logging.ILogger<CatalogGraphQlTypeModule>? logger = null,
        TimeSpan? refreshInterval = null,
        Autheris.Application.Catalog.ICatalogVersionProvider? versionProvider = null,
        bool aggregationEnabled = true,
        bool crossSourceEnabled = false,
        bool metricsEnabled = true,
        GraphQLOptions? graphQlOptions = null,
        Autheris.Application.DataSources.IDataSourceOptionsResolver? transportResolver = null,
        IEpochValidationService? epochService = null,
        TimeProvider? timeProvider = null)
    {
        _transportResolver = transportResolver;
        _epochService = epochService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _aggregationEnabled = aggregationEnabled;
        _crossSourceEnabled = crossSourceEnabled;
        _metricsEnabled = metricsEnabled;
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _relationRepo = relationRepo ?? throw new ArgumentNullException(nameof(relationRepo));
        _logger = logger;
        _refreshInterval = refreshInterval ?? TimeSpan.Zero;
        _versionProvider = versionProvider;
        _graphQlOptions = graphQlOptions ?? new GraphQLOptions();
    }

    internal static string BuildOffsetDescription(int maxAllowedOffset) =>
        maxAllowedOffset <= 0
            ? "Offset pagination is not supported."
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Offset pagination is supported up to offset {maxAllowedOffset}.");

    /// <summary>
    /// Issue #447 the schema omits tables whose source does not allow GraphQL, so the schema never names a source the
    /// transport may not read (the gate at the data path stays the enforcement point). <paramref name="record"/> notes each decision.
    /// </summary>
    private Func<TableMetadata, bool>? GetTableFilter(System.Collections.Concurrent.ConcurrentDictionary<string, bool>? record = null)
    {
        var inner = GetPartitionFilter();
        var resolver = _transportResolver;
        if (resolver is null)
        {
            return inner;
        }

        return meta =>
        {
            var source = Autheris.Application.Sql.Services.DataSourceAllowlistGuard.LogicalDataSource(meta);
            var visible = Autheris.Application.DataSources.TransportDiscoveryFilter.IsSourceVisible(
                resolver, Autheris.Domain.Options.DataSources.DataSourceTransportFlags.GraphQL, source);
            if (record is not null)
            {
                record[source] = visible;
            }

            return visible && (inner is null || inner(meta));
        };
    }

    /// <summary>True when a profile change flipped the GraphQL visibility of a source the schema was built with (forces a rebuild).</summary>
    private bool TransportVisibilityChanged() => TransportVisibilityChanged(_transportSeen);

    private bool TransportVisibilityChanged(System.Collections.Concurrent.ConcurrentDictionary<string, bool> seen)
    {
        var resolver = _transportResolver;
        if (resolver is null)
        {
            return false;
        }

        foreach (var (source, wasVisible) in seen)
        {
            if (Autheris.Application.DataSources.TransportDiscoveryFilter.IsSourceVisible(
                    resolver, Autheris.Domain.Options.DataSources.DataSourceTransportFlags.GraphQL, source) != wasVisible)
            {
                return true;
            }
        }

        return false;
    }

    private Func<TableMetadata, bool>? GetPartitionFilter()
    {
        if (_graphQlOptions.SchemaPartitioning == GraphQlSchemaPartitioningMode.None)
        {
            return null;
        }

        if (_graphQlOptions.SchemaPartitioning == GraphQlSchemaPartitioningMode.ByConsentScope)
        {
            var allowedDomains = new HashSet<string>(_graphQlOptions.PartitionAllowedDomains, StringComparer.OrdinalIgnoreCase);
            var allowedScopes = new HashSet<string>(_graphQlOptions.PartitionAllowedConsentScopes, StringComparer.OrdinalIgnoreCase);

            return meta =>
            {
                if (allowedDomains.Count > 0 && allowedDomains.Contains(meta.Identifier.Domain))
                {
                    return true;
                }
                if (allowedScopes.Count > 0 && meta.Table?.Tags != null && meta.Table.Tags.Any(s => allowedScopes.Contains(s)))
                {
                    return true;
                }
                return false;
            };
        }

        if (_graphQlOptions.SchemaPartitioning == GraphQlSchemaPartitioningMode.ByPermissionTier)
        {
            var allowedDomains = new HashSet<string>(_graphQlOptions.PartitionAllowedDomains, StringComparer.OrdinalIgnoreCase);
            return meta => allowedDomains.Count == 0 || allowedDomains.Contains(meta.Identifier.Domain);
        }

        return null;
    }

    public event EventHandler<EventArgs>? TypesChanged;

    internal string? BuiltFingerprint => _built.Fingerprint;

    /// <summary>BUG #157: omissions of the schema actually served; an immutable snapshot swapped after each build.</summary>
    public IReadOnlyList<CatalogOmittedTable> OmittedTables => _built.OmittedTables;

    internal Task<bool> CheckForCatalogChangesAsync(CancellationToken ct = default) =>
        CheckForCatalogChangesAsync(fromTimer: false, forceFull: false, ct);

    private bool ElapsedAtLeast(long since, TimeSpan span) =>
        since != NoTimestamp && _timeProvider.GetElapsedTime(since, _timeProvider.GetTimestamp()) >= span;

    /// <param name="fromTimer">True for the refresh timer: the tick is skipped while a check still runs.</param>
    /// <param name="forceFull">Skips the unchanged-version early exit; for fixtures that write catalog rows without a version bump.</param>
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
                Volatile.Write(ref _pendingBuild, null);
                Volatile.Write(ref _deferralStartTimestamp, NoTimestamp);
            }

            Autheris.Application.Catalog.CatalogVersion? currentVer = null;
            if (_versionProvider != null)
            {
                try
                {
                    currentVer = await _versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
                    if (!transportChanged &&
                        !forceFull &&
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

            if (!transportChanged && string.Equals(current, _built.Fingerprint, StringComparison.Ordinal))
            {
                if (currentVer.HasValue)
                {
                    Volatile.Write(ref _builtStructureVersion, currentVer.Value.StructureVersion);
                    Volatile.Write(ref _builtContentVersion, currentVer.Value.ContentVersion);
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
            }
        }

        return changed;
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
            _refreshTimer ??= _timeProvider.CreateTimer(
                _ => _ = RunTimerCheckAsync(),
                null,
                _refreshInterval,
                _refreshInterval);
        }
    }

    private async Task RunTimerCheckAsync()
    {
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
            return;
        }

        var elapsedMs = (long)_timeProvider.GetElapsedTime(start, _timeProvider.GetTimestamp()).TotalMilliseconds;
        _logger?.LogInformation(
            "GraphQL catalog schema build finished: tables={Tables} omitted={Omitted} elapsedMs={ElapsedMs} phase=rebuild",
            Volatile.Read(ref _lastBuiltTables),
            Volatile.Read(ref _lastBuiltOmitted),
            elapsedMs);
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }
    }

    public async ValueTask<IReadOnlyCollection<ITypeSystemMember>> CreateTypesAsync(
        IDescriptorContext context,
        CancellationToken cancellationToken)
    {
        CatalogSchemaModel schemaModel;
        long buildStartTimestamp = _timeProvider.GetTimestamp();
        long lastProgressTimestamp = buildStartTimestamp;

        // Issue #786: the request is served from here on, so a change seen from now on may queue exactly one more rebuild.
        // The latch is released before the pending model is taken: a change that lands afterwards is either taken by
        // this build or raises the next (trailing) request.
        Interlocked.Exchange(ref _rebuildRequested, 0);
        Volatile.Write(ref _deferralStartTimestamp, NoTimestamp);
        Volatile.Write(ref _lastCheckSawChange, 0);
        Volatile.Write(ref _buildStartTimestamp, buildStartTimestamp);
        var trigger = Interlocked.Exchange(ref _everBuilt, 1) == 0
            ? "startup"
            : Interlocked.Exchange(ref _coalescedSinceBuild, 0) > 0 ? "coalesced" : "change";
        _logger?.LogInformation("GraphQL catalog schema build started: trigger={Trigger}", trigger);
        try
        {
            var pending = Interlocked.Exchange(ref _pendingBuild, null);
            if (pending != null && !TransportVisibilityChanged(pending.TransportSeen))
            {
                schemaModel = pending.Model;
                _transportSeen = pending.TransportSeen;
                _currentSchemaModel = schemaModel;
                CurrentLookup = GetTableMetadataForField;
                _built = new BuiltState(pending.Fingerprint, schemaModel.OmittedTables);
                if (pending.CatalogVersion.HasValue)
                {
                    Volatile.Write(ref _builtStructureVersion, pending.CatalogVersion.Value.StructureVersion);
                    Volatile.Write(ref _builtContentVersion, pending.CatalogVersion.Value.ContentVersion);
                }
                else if (_versionProvider != null)
                {
                    var catVer = await _versionProvider.GetCatalogVersionAsync(cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _builtStructureVersion, catVer.StructureVersion);
                    Volatile.Write(ref _builtContentVersion, catVer.ContentVersion);
                }
            }
            else
            {
                var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
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
                _lastFullCheckTimestamp = _timeProvider.GetTimestamp();
                _transportSeen = seen;
                _currentSchemaModel = schemaModel;
                CurrentLookup = GetTableMetadataForField;
                _built = new BuiltState(schemaModel.Fingerprint(), schemaModel.OmittedTables);
                if (_versionProvider != null)
                {
                    var catVer = await _versionProvider.GetCatalogVersionAsync(cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _builtStructureVersion, catVer.StructureVersion);
                    Volatile.Write(ref _builtContentVersion, catVer.ContentVersion);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "GraphQL catalog schema could not be built; serving the schema without catalog tables until the catalog is reachable.");
            schemaModel = CatalogSchemaModel.Empty;
            Interlocked.Exchange(ref _rebuildRequested, 0);
            Volatile.Write(ref _pendingBuild, null);
            _built = new BuiltState(null, []);
            Volatile.Write(ref _builtStructureVersion, -1);
            Volatile.Write(ref _builtContentVersion, -1);
        }

        Volatile.Write(ref _lastBuiltTables, schemaModel.Tables.Count);
        Volatile.Write(ref _lastBuiltOmitted, schemaModel.OmittedTables.Count);
        EnsureRefreshTimer();

        var types = new List<ITypeSystemMember>();

        types.Add(new EnumType(d =>
        {
            d.Name("AutherisSortDirection");
            d.Description("Specifies ordering direction.");
            d.Value("ASC").Description("Ascending order.");
            d.Value("DESC").Description("Descending order.");
        }));

        RegisterOperatorFilterTypes(types);

        AggregateGraphQlTypes.RegisterShared(types);
        ConnectionGraphQlTypes.RegisterShared(types);
        if (_metricsEnabled)
        {
            MetricGraphQlTypes.Register(types);
        }

        int registeredTables = 0;
        foreach (var table in schemaModel.Tables)
        {
            types.Add(new InputObjectType(d =>
            {
                d.Name(table.FilterTypeName);
                d.Description($"Filter input for {table.TypeName}.");
                d.Field("and").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(table.FilterTypeName))));
                d.Field("or").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(table.FilterTypeName))));
                d.Field("not").Type(new NamedTypeNode(table.FilterTypeName));

                foreach (var col in table.Columns)
                {
                    d.Field(col.FieldName).Type(new NamedTypeNode(GetFilterTypeName(col.FieldType)));
                }
            }));

            types.Add(new InputObjectType(d =>
            {
                d.Name(table.OrderByTypeName);
                d.Description($"Order-by input for {table.TypeName}.");
                foreach (var col in table.Columns)
                {
                    d.Field(col.FieldName).Type(new NamedTypeNode("AutherisSortDirection"));
                }
            }));

            types.Add(new ObjectType(d =>
            {
                d.Name(table.TypeName);
                d.Description($"Entity type for {table.Metadata.Identifier}.");
                var entityLogicalSource = Autheris.Application.Sql.Services.DataSourceAllowlistGuard.LogicalDataSource(table.Metadata);
                d.Extend().OnBeforeCreate(definition =>
                    definition.Features.Set(new Autheris.GraphQL.Interceptors.TransportSourceFeature(entityLogicalSource)));

                foreach (var col in table.Columns)
                {
                    var scalarName = GetScalarName(col.FieldType);
                    ITypeNode typeNode = col.IsNullable
                        ? new NamedTypeNode(scalarName)
                        : new NonNullTypeNode(new NamedTypeNode(scalarName));

                    var fieldDesc = d.Field(col.FieldName)
                        .Type(typeNode)
                        .Resolve(ctx => ResolveColumnValue(ctx, col));

                    if (!string.IsNullOrWhiteSpace(col.Description))
                    {
                        fieldDesc.Description(col.Description);
                    }
                }

                foreach (var rel in table.Relations)
                {
                    CatalogRelationRegistry.Register(table.TypeName, rel.FieldName, rel.Cardinality);
                    if (rel.IsList)
                    {
                        d.Field(rel.FieldName)
                            .Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(rel.TargetTypeName))))
                            .Argument("where", a => a.Type(new NamedTypeNode($"{rel.TargetTypeName}_filter")))
                            .Argument("orderBy", a => a.Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode($"{rel.TargetTypeName}_order_by")))))
                            .Argument("first", a => a.Type(new NamedTypeNode("Int")).DefaultValue(100))
                            .Argument("offset", a => a.Type(new NamedTypeNode("Int")).DefaultValue(0))
                            .Resolve(ctx => ResolveRelationValue(ctx, rel));
                    }
                    else
                    {
                        d.Field(rel.FieldName)
                            .Type(new NamedTypeNode(rel.TargetTypeName))
                            .Argument("where", a => a.Type(new NamedTypeNode($"{rel.TargetTypeName}_filter")))
                            .Resolve(ctx => ResolveRelationValue(ctx, rel));
                    }
                }
            }));

            if (_aggregationEnabled)
            {
                AggregateGraphQlTypes.RegisterTable(types, table);
            }

            ConnectionGraphQlTypes.RegisterTable(types, table);
            registeredTables++;
            CatalogSchemaModel.MaybeLogBuildProgress(_logger, _timeProvider, buildStartTimestamp, ref lastProgressTimestamp, registeredTables, "rebuild");
        }

        if (schemaModel.Tables.Count > 0)
        {
            types.Add(new ObjectTypeExtension(d =>
            {
                d.Name("Query");

                if (_metricsEnabled)
                {
                    MetricGraphQlTypes.AddQueryField(d);
                }

                // I417-SC-A07: the offset text comes only from the numeric MaxAllowedOffset option.
                var offsetText = BuildOffsetDescription(_graphQlOptions.MaxAllowedOffset);
                foreach (var table in schemaModel.Tables)
                {
                    var pk = table.Metadata.PrimaryKeyColumns.Count > 0 ? table.Metadata.PrimaryKeyColumns[0] : "id";
                    if (string.IsNullOrWhiteSpace(pk) || !System.Text.RegularExpressions.Regex.IsMatch(pk, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                    {
                        pk = "id";
                    }
                    d.Field(table.QueryFieldName)
                        .Description($"Query {table.TypeName}. Keyset paging can be performed via where: {{ {pk}: {{ gt: $last{pk} }} }}, orderBy: [{{ {pk}: ASC }}]. {offsetText}")
                        .Type(new NonNullTypeNode(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(table.TypeName)))))
                        .Argument("where", a => a.Type(new NamedTypeNode(table.FilterTypeName)))
                        .Argument("orderBy", a => a.Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(table.OrderByTypeName)))))
                        .Argument("first", a => a.Type(new NamedTypeNode("Int")).DefaultValue(100))
                        .Argument("offset", a => a.Type(new NamedTypeNode("Int")).DefaultValue(0))
                        .Resolve(async ctx => await ResolveRootQueryAsync(ctx, table, schemaModel).ConfigureAwait(false));

                    if (_aggregationEnabled)
                    {
                        AggregateGraphQlTypes.AddQueryField(d, table);
                    }

                    ConnectionGraphQlTypes.AddQueryField(d, table, schemaModel);

                    // Issue #447: every root field of this table names its logical data source, so the transport gate
                    // middleware can check the whole operation before any resolver runs.
                    var logicalSource = Autheris.Application.Sql.Services.DataSourceAllowlistGuard.LogicalDataSource(table.Metadata);
                    var rootFieldNames = new List<string> { table.QueryFieldName, $"{table.QueryFieldName}Connection" };
                    if (_aggregationEnabled)
                    {
                        rootFieldNames.Add(AggregateTypeNames.For(table.TypeName).Field);
                    }

                    foreach (var rootFieldName in rootFieldNames)
                    {
                        d.Field(rootFieldName).Extend().OnBeforeCreate(
                            definition =>
                            {
                                definition.Features.Set(new Autheris.GraphQL.Interceptors.TransportSourceFeature(logicalSource));
                                definition.Features.Set(new CatalogRootFieldFeature(table, schemaModel));
                            });
                    }
                }
            }));
        }

        return types;
    }

    private static void RegisterOperatorFilterTypes(List<ITypeSystemMember> types)
    {
        static void AddComparableFilter(List<ITypeSystemMember> list, string name, string scalar, bool includeStringOps = false)
        {
            list.Add(new InputObjectType(d =>
            {
                d.Name(name);
                foreach (var op in new[] { "eq", "neq", "gt", "gte", "lt", "lte" })
                {
                    d.Field(op).Type(new NamedTypeNode(scalar));
                }
                d.Field("in").Type(new ListTypeNode(new NamedTypeNode(scalar)));
                d.Field("nin").Type(new ListTypeNode(new NamedTypeNode(scalar)));
                d.Field("isNull").Type(new NamedTypeNode("Boolean"));
                if (includeStringOps)
                {
                    d.Field("contains").Type(new NamedTypeNode("String"));
                    d.Field("startsWith").Type(new NamedTypeNode("String"));
                    d.Field("endsWith").Type(new NamedTypeNode("String"));
                }
            }));
        }

        AddComparableFilter(types, "AutherisStringFilter", "String", includeStringOps: true);
        AddComparableFilter(types, "AutherisIntFilter", "Int");
        AddComparableFilter(types, "AutherisLongFilter", "Long");
        AddComparableFilter(types, "AutherisFloatFilter", "Float");
        AddComparableFilter(types, "AutherisDecimalFilter", "Decimal");
        AddComparableFilter(types, "AutherisDateTimeFilter", "String");

        types.Add(new InputObjectType(d =>
        {
            d.Name("AutherisBooleanFilter");
            d.Field("eq").Type(new NamedTypeNode("Boolean"));
            d.Field("neq").Type(new NamedTypeNode("Boolean"));
            d.Field("isNull").Type(new NamedTypeNode("Boolean"));
        }));
    }

    internal static string GetScalarName(CatalogFieldType type) => type switch
    {
        CatalogFieldType.String => "String",
        CatalogFieldType.Int => "Int",
        CatalogFieldType.Long => "Long",
        CatalogFieldType.Float => "Float",
        CatalogFieldType.Decimal => "Decimal",
        CatalogFieldType.Boolean => "Boolean",
        CatalogFieldType.DateTime => "String",
        _ => "String"
    };

    private static string GetFilterTypeName(CatalogFieldType type) => type switch
    {
        CatalogFieldType.String => "AutherisStringFilter",
        CatalogFieldType.Int => "AutherisIntFilter",
        CatalogFieldType.Long => "AutherisLongFilter",
        CatalogFieldType.Float => "AutherisFloatFilter",
        CatalogFieldType.Decimal => "AutherisDecimalFilter",
        CatalogFieldType.Boolean => "AutherisBooleanFilter",
        CatalogFieldType.DateTime => "AutherisDateTimeFilter",
        _ => "AutherisStringFilter"
    };

    private static object? ReportMasked(IResolverContext ctx, CatalogColumnField col)
    {
        ctx.ReportError(ErrorBuilder.New()
            .SetMessage($"Column '{col.ColumnName}' is masked.")
            .SetCode("MASKED")
            .SetPath(ctx.Path)
            .Build());
        return null;
    }

    /// <summary>Request-scoped set of masked column keys ("rootAlias/relationAlias/column"), see <see cref="RegisterMaskedColumns"/>.</summary>
    private const string MaskedColumnKeysContextKey = "AutherisMaskedColumnKeys";

    /// <summary>
    /// Splits a response path into its field names (list indexes dropped) and its documented pointer form,
    /// e.g. customers[0].orders[1].amount -> (customers, orders, amount) and "/customers/0/orders/1/amount".
    /// </summary>
    private static (List<string> Names, string Pointer) DescribePath(HotChocolate.Path path)
    {
        var segments = new List<string>();
        var names = new List<string>();
        for (var current = path; current is not null && !current.IsRoot; current = current.Parent)
        {
            switch (current)
            {
                case HotChocolate.NamePathSegment named:
                    segments.Add(named.Name);
                    names.Add(named.Name);
                    break;
                case HotChocolate.IndexerPathSegment indexed:
                    segments.Add(indexed.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }

        segments.Reverse();
        names.Reverse();
        return (names, "/" + string.Join('/', segments));
    }

    private static string MaskedColumnKey(IEnumerable<string> names, string column) => string.Join('/', names) + "\u001f" + column;

    /// <summary>
    /// Remembers which selected columns the governed tree masks for the caller, per request and per root field, so the
    /// column resolvers can report their response path into <c>extensions.maskedFields</c>.
    /// </summary>
    private static void RegisterMaskedColumns(IResolverContext ctx, IReadOnlyList<TreeMaskedColumn>? masked)
    {
        if (masked is null || masked.Count == 0)
        {
            return;
        }

        System.Collections.Concurrent.ConcurrentDictionary<string, byte> keys;
        lock (ctx.ContextData)
        {
            if (!ctx.ContextData.TryGetValue(MaskedColumnKeysContextKey, out var existing) ||
                existing is not System.Collections.Concurrent.ConcurrentDictionary<string, byte> found)
            {
                found = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
                ctx.ContextData[MaskedColumnKeysContextKey] = found;
            }
            keys = found;
        }

        foreach (var m in masked)
        {
            keys[MaskedColumnKey(m.Path.Prepend(ctx.ResponseName), m.Column)] = 0;
        }
    }

    private static void RecordMaskedColumn(IResolverContext ctx, CatalogColumnField col)
    {
        if (!ctx.ContextData.TryGetValue(MaskedColumnKeysContextKey, out var keysObj) ||
            keysObj is not System.Collections.Concurrent.ConcurrentDictionary<string, byte> keys ||
            !ctx.ContextData.TryGetValue(Autheris.GraphQL.Interceptors.MaskedFieldsMiddleware.ContextKey, out var bagObj) ||
            bagObj is not System.Collections.Concurrent.ConcurrentBag<string> bag)
        {
            return;
        }

        var (names, pointer) = DescribePath(ctx.Path);
        if (names.Count == 0)
        {
            return;
        }

        names.RemoveAt(names.Count - 1);
        if (keys.ContainsKey(MaskedColumnKey(names, col.ColumnName)))
        {
            bag.Add(pointer);
        }
    }

    private static object? ResolveColumnValue(IResolverContext ctx, CatalogColumnField col)
    {
        var parent = ctx.Parent<JsonElement>();
        if (parent.ValueKind != JsonValueKind.Object) return null;
        RecordMaskedColumn(ctx, col);
        if (!parent.TryGetProperty(col.ColumnName, out var prop) && !parent.TryGetProperty(col.FieldName, out prop)) return null;
        if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return col.FieldType switch
        {
            CatalogFieldType.Int => prop.ValueKind == JsonValueKind.Number
                ? (prop.TryGetInt32(out var n) ? n : (int.TryParse(prop.GetRawText(), System.Globalization.NumberStyles.Integer, inv, out var pi) ? pi : ReportMasked(ctx, col)))
                : (int.TryParse(prop.GetString(), System.Globalization.NumberStyles.Integer, inv, out var strN) ? strN : (IsHmacHexString(prop.GetString()) ? prop.GetString() : ReportMasked(ctx, col))),
            CatalogFieldType.Long => prop.ValueKind == JsonValueKind.Number
                ? (prop.TryGetInt64(out var l) ? l : (long.TryParse(prop.GetRawText(), System.Globalization.NumberStyles.Integer, inv, out var pl) ? pl : ReportMasked(ctx, col)))
                : (long.TryParse(prop.GetString(), System.Globalization.NumberStyles.Integer, inv, out var strL) ? strL : (IsHmacHexString(prop.GetString()) ? prop.GetString() : ReportMasked(ctx, col))),
            CatalogFieldType.Float => prop.ValueKind == JsonValueKind.Number
                ? (prop.TryGetDouble(out var d) ? d : (double.TryParse(prop.GetRawText(), System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, inv, out var pd) ? pd : ReportMasked(ctx, col)))
                : (double.TryParse(prop.GetString(), System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, inv, out var strD) ? strD : ReportMasked(ctx, col)),
            CatalogFieldType.Decimal => prop.ValueKind == JsonValueKind.Number
                ? (prop.TryGetDecimal(out var m) ? m : (decimal.TryParse(prop.GetRawText(), System.Globalization.NumberStyles.Number, inv, out var pm) ? pm : ReportMasked(ctx, col)))
                : (decimal.TryParse(prop.GetString(), System.Globalization.NumberStyles.Number, inv, out var strM) ? strM : ReportMasked(ctx, col)),
            CatalogFieldType.Boolean => prop.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => prop.TryGetInt64(out var i) ? i != 0 : (prop.TryGetDouble(out var dVal) ? Math.Abs(dVal) > double.Epsilon : ReportMasked(ctx, col)),
                JsonValueKind.String => string.Equals(prop.GetString(), "true", StringComparison.OrdinalIgnoreCase) || prop.GetString() == "1" ? true
                    : (string.Equals(prop.GetString(), "false", StringComparison.OrdinalIgnoreCase) || prop.GetString() == "0" ? false : ReportMasked(ctx, col)),
                _ => ReportMasked(ctx, col)
            },
            CatalogFieldType.String => prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.GetRawText(),
                _ => ReportMasked(ctx, col)
            },
            CatalogFieldType.DateTime => prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.GetRawText(),
            _ => prop.ToString()
        };
    }

    private static object? ResolveRelationValue(IResolverContext ctx, CatalogRelationField rel)
    {
        var parent = ctx.Parent<JsonElement>();
        if (parent.ValueKind != JsonValueKind.Object) return null;
        var key = ctx.ResponseName;
        if (!parent.TryGetProperty(key, out var prop) && !parent.TryGetProperty(rel.FieldName, out prop)) return null;
        if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;

        if (rel.IsList)
        {
            if (prop.ValueKind != JsonValueKind.Array) return null;
            var list = new List<JsonElement>(prop.GetArrayLength());
            foreach (var item in prop.EnumerateArray()) list.Add(item);
            return list;
        }

        return prop;
    }


    private static async Task<IReadOnlyList<JsonElement>> ResolveRootQueryAsync(
        IResolverContext ctx,
        CatalogTableType table,
        CatalogSchemaModel schema)
    {
        var treeService = ctx.Service<IGovernedTreeQueryService>();
        var httpContext = ctx.Service<IHttpContextAccessor>()?.HttpContext;
        var principal = (ctx.ContextData.TryGetValue(nameof(ClaimsPrincipal), out var cp) && cp is ClaimsPrincipal p)
            ? p
            : httpContext?.User ?? new ClaimsPrincipal();

        IReadOnlyDictionary<string, string[]>? headers = null;
        if (httpContext?.Request?.Headers is { Count: > 0 } reqHeaders)
        {
            headers = reqHeaders.ToDictionary(h => h.Key, h => h.Value.Where(v => v != null).Select(v => v!).ToArray(), StringComparer.OrdinalIgnoreCase);
        }

        int maxResponseRows = 1000;
        try
        {
            var options = ctx.Service<IOptions<GatewayOptions>>();
            if (options?.Value?.GraphQL?.MaxResponseRows is { } max && max > 0)
            {
                maxResponseRows = max;
            }
        }
        catch
        {
            // Fallback to default
        }

        string opId;
        lock (ctx.ContextData)
        {
            if (!ctx.ContextData.TryGetValue("AutherisOperationId", out var opIdObj) || opIdObj is not string existingOpId)
            {
                opId = Guid.NewGuid().ToString("N");
                ctx.ContextData["AutherisOperationId"] = opId;
            }
            else
            {
                opId = existingOpId;
            }
        }

        try
        {
            var queryNode = GraphQlTreeBuilder.BuildTree(ctx, table, schema, maxResponseRows, out var pageRangeViolated);
            var observer = ctx.ContextData.TryGetValue(Autheris.GraphQL.Interceptors.SlowListsMiddleware.ContextKey, out var obsObj) && obsObj is Autheris.GraphQL.Interceptors.SlowListsAccumulator acc
                ? acc.ForRoot(ctx.ResponseName)
                : null;
            using var doc = await (observer != null
                ? treeService.ExecuteAsync(principal, queryNode, headers, opId, observer, ctx.RequestAborted)
                : treeService.ExecuteAsync(principal, queryNode, headers, opId, ctx.RequestAborted))
                .ConfigureAwait(false);

            if (pageRangeViolated)
            {
                // 08 (#330): the tree was built with a zero-row page and has just passed table authorization, so a denied table
                // answers ACCESS_DENIED for every page size; only an authorized caller learns the fixed page-range error.
                throw new GatewayPageRangeException();
            }

            RegisterMaskedColumns(ctx, treeService.GetMaskedColumns(principal, queryNode, opId));

            var root = doc.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<JsonElement>();
            }

            var list = new List<JsonElement>(root.GetArrayLength());
            foreach (var item in root.EnumerateArray())
            {
                list.Add(item);
            }

            var isTruncated = false;
            var maxRowsReached = false;
            var requestedLimit = queryNode.Limit;
            if (list.Count > requestedLimit)
            {
                isTruncated = true;
                maxRowsReached = requestedLimit >= maxResponseRows;
                list.RemoveAt(list.Count - 1);
            }
            else if (list.Count >= maxResponseRows)
            {
                isTruncated = true;
                maxRowsReached = true;
            }

            lock (ctx.ContextData)
            {
                var currentRows = ctx.ContextData.TryGetValue("AutherisRowsReturned", out var rObj) && rObj is long rVal ? rVal : 0L;
                ctx.ContextData["AutherisRowsReturned"] = currentRows + list.Count;

                if (isTruncated)
                {
                    ctx.ContextData[PaginationTruncationMiddleware.ContextKey] = true;
                    ctx.ContextData[PaginationTruncationMiddleware.MaxRowsReachedKey] = maxRowsReached;
                    ctx.ContextData[PaginationTruncationMiddleware.PageSizeKey] = list.Count;
                }
            }

            return list;
        }
        catch (AggregationColumnUnavailableException)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage(AggregationColumnUnavailableException.FixedMessage)
                .SetCode(AggregationColumnUnavailableException.ErrorCodeConst)
                .Build());
        }
        catch (AggregationRowCapExceededException ex)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage(ex.Message).SetCode(AggregationRowCapExceededException.ErrorCodeConst).Build());
        }
        catch (GatewayPageRangeException)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage(GatewayPageRangeException.FixedMessage).SetCode("INVALID_QUERY_OPTION").Build());
        }
        catch (GatewayInvalidQueryException ex)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage(ex.Message).SetCode(ex.ErrorCode).Build());
        }
        catch (TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RequiredFilterColumnMissing))
                .SetCode(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RequiredFilterColumnMissing)
                .Build());
        }
        catch (Autheris.Application.DataSources.Relations.CrossSourceRelationException ex)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage(ex.Message).SetCode(ex.ErrorCode).Build());
        }
        catch (GatewayForbiddenException)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage("Access denied.").SetCode("ACCESS_DENIED").Build());
        }
        catch (GatewayThrottledException ex)
        {
            if (httpContext != null && !httpContext.Response.HasStarted)
            {
                httpContext.Response.Headers.RetryAfter = ex.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage(ex.Message)
                .SetCode("TOO_MANY_REQUESTS")
                .SetExtension("retryAfterSeconds", ex.RetryAfterSeconds)
                .Build());
        }
        catch (GatewaySecurityException ex) when (string.Equals(ex.ErrorCode, "RESPONSE_TOO_LARGE", StringComparison.OrdinalIgnoreCase))
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage(ex.Message).SetCode("RESPONSE_TOO_LARGE").Build());
        }
        catch (Exception ex)
        {
            var classified = DataAccessErrorClassifier.Classify(ex);
            if (classified == DataAccessErrorKind.Timeout)
            {
                throw new GraphQLException(ErrorBuilder.New().SetMessage("The database query timed out.").SetCode("TIMEOUT").Build());
            }
            if (classified == DataAccessErrorKind.Unavailable)
            {
                throw new GraphQLException(ErrorBuilder.New().SetMessage("The database is currently unavailable.").SetCode("UNAVAILABLE").Build());
            }
            throw;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex HmacRegex =
        new("^[a-fA-F0-9]{64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsHmacHexString(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length == 64 && HmacRegex.IsMatch(value);
}

