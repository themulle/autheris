using System.Collections.Frozen;
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

public sealed partial class CatalogGraphQlTypeModule : ITypeModule, IDisposable, ICatalogSchemaDiagnostics
{
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly ITableRelationRepository _relationRepo;
    private readonly Autheris.Application.Catalog.ICatalogVersionProvider? _versionProvider;
    private readonly Microsoft.Extensions.Logging.ILogger<CatalogGraphQlTypeModule>? _logger;
    private readonly TimeSpan _refreshInterval;
    private readonly bool _aggregationEnabled;
    private readonly string _defaultBucketTimeZone;
    private readonly bool _crossSourceEnabled;
    private readonly bool _metricsEnabled;
    private readonly object _timerLock = new();
    private ITimer? _refreshTimer;
    private sealed record BuiltState(
        string? Fingerprint,
        IReadOnlyList<CatalogOmittedTable> OmittedTables,
        CatalogSchemaModel? Model = null,
        Autheris.Application.Catalog.CatalogVersion? Version = null,
        System.Collections.Concurrent.ConcurrentDictionary<string, bool>? TransportSeen = null);
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


    private readonly CatalogTableFingerprintCache _tableFingerprintCache = new();
    internal CatalogTableFingerprintCache FingerprintCache => _tableFingerprintCache;

    private sealed record CachedColumnRecipe(
        CatalogColumnField Column,
        ITypeNode EntityFieldType,
        ITypeNode FilterFieldType);

    private sealed record CachedTableRecipe(
        string Hash,
        string LogicalSource,
        string FilterDescription,
        string OrderByDescription,
        string EntityDescription,
        IReadOnlyList<CachedColumnRecipe> Columns,
        NamedTypeNode FilterTypeNode,
        NamedTypeNode OrderByTypeNode,
        NamedTypeNode EntityTypeNode,
        ListTypeNode FilterListTypeNode,
        ListTypeNode OrderByListTypeNode);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<TableIdentifier, CachedTableRecipe> _tableRecipeCache = new();
    private int _lastBuildRecipeRebuildCount;
    private int _totalRecipeRebuildCount;

    internal int LastBuildRecipeRebuildCount => Volatile.Read(ref _lastBuildRecipeRebuildCount);
    internal int TotalRecipeRebuildCount => Volatile.Read(ref _totalRecipeRebuildCount);

    private static readonly NamedTypeNode SortDirectionTypeNode = new("AutherisSortDirection");
    private static readonly System.Collections.Frozen.FrozenDictionary<CatalogFieldType, NamedTypeNode> FilterTypeNodes =
        new Dictionary<CatalogFieldType, NamedTypeNode>
        {
            [CatalogFieldType.String] = new("AutherisStringFilter"),
            [CatalogFieldType.Int] = new("AutherisIntFilter"),
            [CatalogFieldType.Long] = new("AutherisLongFilter"),
            [CatalogFieldType.Float] = new("AutherisFloatFilter"),
            [CatalogFieldType.Decimal] = new("AutherisDecimalFilter"),
            [CatalogFieldType.Boolean] = new("AutherisBooleanFilter"),
            [CatalogFieldType.DateTime] = new("AutherisDateTimeFilter"),
        }.ToFrozenDictionary();

    private static readonly System.Collections.Frozen.FrozenDictionary<CatalogFieldType, (ITypeNode Nullable, ITypeNode NonNull)> ScalarTypeNodes =
        new Dictionary<CatalogFieldType, (ITypeNode, ITypeNode)>
        {
            [CatalogFieldType.String] = CreateScalarPair("String"),
            [CatalogFieldType.Int] = CreateScalarPair("Int"),
            [CatalogFieldType.Long] = CreateScalarPair("Long"),
            [CatalogFieldType.Float] = CreateScalarPair("Float"),
            [CatalogFieldType.Decimal] = CreateScalarPair("Decimal"),
            [CatalogFieldType.Boolean] = CreateScalarPair("Boolean"),
            [CatalogFieldType.DateTime] = CreateScalarPair("String"),
        }.ToFrozenDictionary();

    private static (ITypeNode Nullable, ITypeNode NonNull) CreateScalarPair(string scalarName)
    {
        var nullable = new NamedTypeNode(scalarName);
        return (nullable, new NonNullTypeNode(nullable));
    }

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
    private readonly Autheris.Application.Catalog.ICatalogSnapshotProvider? _snapshotProvider;

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
        TimeProvider? timeProvider = null,
        IEventBus? eventBus = null,
        Autheris.Application.Catalog.ICatalogSnapshotProvider? snapshotProvider = null,
        string defaultBucketTimeZone = AggregationOptions.UtcTimeZoneId)
    {
        _defaultBucketTimeZone = defaultBucketTimeZone;
        _transportResolver = transportResolver;
        _epochService = epochService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _snapshotProvider = snapshotProvider;
        _aggregationEnabled = aggregationEnabled;
        _crossSourceEnabled = crossSourceEnabled;
        _metricsEnabled = metricsEnabled;
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _relationRepo = relationRepo ?? throw new ArgumentNullException(nameof(relationRepo));
        _logger = logger;
        _refreshInterval = refreshInterval ?? TimeSpan.Zero;
        _versionProvider = versionProvider;
        _graphQlOptions = graphQlOptions ?? new GraphQLOptions();
        _changeSubscription = SubscribeToCatalogChanges(eventBus); // last: a signal may arrive at once (issue #802)
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

    public void Dispose()
    {
        lock (_timerLock)
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }

        DisposeChangeSignal();
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
        // Issue #802: the build is marked in flight before the request is released, so the schema status never has a gap.
        Interlocked.Exchange(ref _buildInFlight, 1);
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
            Autheris.Application.Catalog.CatalogVersion? publishedVersion = null;
            var pending = Interlocked.Exchange(ref _pendingBuild, null);
            if (pending != null && !TransportVisibilityChanged(pending.TransportSeen))
            {
                publishedVersion = pending.CatalogVersion;
                if (!publishedVersion.HasValue && _versionProvider != null)
                {
                    publishedVersion = await _versionProvider.GetCatalogVersionAsync(cancellationToken).ConfigureAwait(false);
                }

                schemaModel = pending.Model;
                _transportSeen = pending.TransportSeen;
                _currentSchemaModel = schemaModel;
                CurrentLookup = GetTableMetadataForField;
                if (publishedVersion.HasValue)
                {
                    Volatile.Write(ref _builtStructureVersion, publishedVersion.Value.StructureVersion);
                    Volatile.Write(ref _builtContentVersion, publishedVersion.Value.ContentVersion);
                }
                _built = new BuiltState(pending.Fingerprint, schemaModel.OmittedTables, schemaModel, publishedVersion, pending.TransportSeen);
            }
            else
            {
                (schemaModel, publishedVersion) = await BuildFreshSchemaModelAsync(cancellationToken).ConfigureAwait(false);
            }

            NotifySchemaModelBuilt(schemaModel, publishedVersion);
        }
        catch (OperationCanceledException)
        {
            // Issue #802: a cancelled build never completes; it must not leave the schema status "building" forever.
            Interlocked.Exchange(ref _buildInFlight, 0);
            throw;
        }
        catch (Exception ex)
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
        int rebuiltRecipes = 0;
        var activeTableIds = new HashSet<TableIdentifier>(schemaModel.Tables.Count);
        foreach (var table in schemaModel.Tables)
        {
            activeTableIds.Add(table.Identifier);
            var tableHash = _tableFingerprintCache.TryGetTableHash(table.Identifier, out var cachedHash)
                ? cachedHash
                : CatalogSchemaModel.ComputeTableFingerprint(table);

            if (!_tableRecipeCache.TryGetValue(table.Identifier, out var recipe) ||
                !string.Equals(recipe.Hash, tableHash, StringComparison.Ordinal))
            {
                recipe = BuildTableRecipe(table, tableHash);
                _tableRecipeCache[table.Identifier] = recipe;
                rebuiltRecipes++;
            }

            RegisterTableTypes(types, table, recipe);
            registeredTables++;
            CatalogSchemaModel.MaybeLogBuildProgress(_logger, _timeProvider, buildStartTimestamp, ref lastProgressTimestamp, registeredTables, "rebuild");
        }

        foreach (var staleId in _tableRecipeCache.Keys)
        {
            if (!activeTableIds.Contains(staleId))
            {
                _tableRecipeCache.TryRemove(staleId, out _);
            }
        }

        Volatile.Write(ref _lastBuildRecipeRebuildCount, rebuiltRecipes);
        Interlocked.Add(ref _totalRecipeRebuildCount, rebuiltRecipes);

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
                    if (!SqlIdentifierGuard.IsValidIdentifier(pk, maxLength: null))
                    {
                        pk = "id";
                    }
                    // Issue #447: every root field of this table names its logical data source, so the transport gate
                    // middleware can check the whole operation before any resolver runs.
                    var logicalSource = Autheris.Application.Sql.Services.DataSourceAllowlistGuard.LogicalDataSource(table.Metadata);
                    var queryField = d.Field(table.QueryFieldName)
                        .Description($"Query {table.TypeName}. Keyset paging can be performed via where: {{ {pk}: {{ gt: $last{pk} }} }}, orderBy: [{{ {pk}: ASC }}]. {offsetText}")
                        .Type(new NonNullTypeNode(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(table.TypeName)))))
                        .Argument("where", a => a.Type(new NamedTypeNode(table.FilterTypeName)))
                        .Argument("orderBy", a => a.Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(table.OrderByTypeName)))))
                        .Argument("first", a => a.Type(new NamedTypeNode("Int")).DefaultValue(100))
                        .Argument("offset", a => a.Type(new NamedTypeNode("Int")).DefaultValue(0))
                        .Resolve(async ctx => await ResolveRootQueryAsync(ctx, table, schemaModel).ConfigureAwait(false));
                    AttachRootFieldFeatures(queryField, logicalSource, table, schemaModel);

                    if (_aggregationEnabled)
                    {
                        var aggregateField = AggregateGraphQlTypes.AddQueryField(d, table);
                        AttachRootFieldFeatures(aggregateField, logicalSource, table, schemaModel);
                    }

                    var connectionField = ConnectionGraphQlTypes.AddQueryField(d, table, schemaModel);
                    AttachRootFieldFeatures(connectionField, logicalSource, table, schemaModel);
                }
            }));
        }

        return types;
    }

    private static CachedTableRecipe BuildTableRecipe(CatalogTableType table, string tableHash)
    {
        var cols = new CachedColumnRecipe[table.Columns.Count];
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            var pair = ScalarTypeNodes.TryGetValue(col.FieldType, out var p) ? p : ScalarTypeNodes[CatalogFieldType.String];
            var entityFieldType = col.IsNullable ? pair.Nullable : pair.NonNull;
            var filterFieldType = FilterTypeNodes.TryGetValue(col.FieldType, out var f) ? f : FilterTypeNodes[CatalogFieldType.String];
            cols[i] = new CachedColumnRecipe(col, entityFieldType, filterFieldType);
        }

        var filterTypeNode = new NamedTypeNode(table.FilterTypeName);
        var orderByTypeNode = new NamedTypeNode(table.OrderByTypeName);
        var entityTypeNode = new NamedTypeNode(table.TypeName);
        return new CachedTableRecipe(
            tableHash,
            Autheris.Application.Sql.Services.DataSourceAllowlistGuard.LogicalDataSource(table.Metadata),
            $"Filter input for {table.TypeName}.",
            $"Order-by input for {table.TypeName}.",
            $"Entity type for {table.Metadata.Identifier}.",
            cols,
            filterTypeNode,
            orderByTypeNode,
            entityTypeNode,
            new ListTypeNode(new NonNullTypeNode(filterTypeNode)),
            new ListTypeNode(new NonNullTypeNode(orderByTypeNode)));
    }

    private void RegisterTableTypes(List<ITypeSystemMember> tableTypes, CatalogTableType table, CachedTableRecipe recipe)
    {
        tableTypes.Add(new InputObjectType(d =>
        {
            d.Name(table.FilterTypeName);
            d.Description(recipe.FilterDescription);
            d.Field("and").Type(recipe.FilterListTypeNode);
            d.Field("or").Type(recipe.FilterListTypeNode);
            d.Field("not").Type(recipe.FilterTypeNode);

            foreach (var col in recipe.Columns)
            {
                d.Field(col.Column.FieldName).Type(col.FilterFieldType);
            }
        }));

        tableTypes.Add(new InputObjectType(d =>
        {
            d.Name(table.OrderByTypeName);
            d.Description(recipe.OrderByDescription);
            foreach (var col in recipe.Columns)
            {
                d.Field(col.Column.FieldName).Type(SortDirectionTypeNode);
            }
        }));

        tableTypes.Add(new ObjectType(d =>
        {
            d.Name(table.TypeName);
            d.Description(recipe.EntityDescription);
            d.Extend().OnBeforeCreate(definition =>
                definition.Features.Set(new Autheris.GraphQL.Interceptors.TransportSourceFeature(recipe.LogicalSource)));

            foreach (var col in recipe.Columns)
            {
                var c = col.Column;
                var fieldDesc = d.Field(c.FieldName)
                    .Type(col.EntityFieldType)
                    .Resolve(ctx => ResolveColumnValue(ctx, c));

                if (!string.IsNullOrWhiteSpace(c.Description))
                {
                    fieldDesc.Description(c.Description);
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
            AggregateGraphQlTypes.RegisterTable(tableTypes, table, _defaultBucketTimeZone);
        }

        ConnectionGraphQlTypes.RegisterTable(tableTypes, table);
    }

    private static void AttachRootFieldFeatures(
        IObjectFieldDescriptor field,
        string logicalSource,
        CatalogTableType table,
        CatalogSchemaModel schemaModel)
    {
        field.Extend().OnBeforeCreate(
            definition =>
            {
                definition.Features.Set(new Autheris.GraphQL.Interceptors.TransportSourceFeature(logicalSource));
                definition.Features.Set(new CatalogRootFieldFeature(table, schemaModel));
            });
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
        var accessor = ctx.Service<IHttpContextAccessor>();
        var httpContext = Autheris.GraphQL.Security.GraphQlCallerPrincipal.HasSessionPrincipalOverride(ctx) ? null : accessor?.HttpContext;
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(ctx, accessor);

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

        if (Autheris.Application.Common.Quota.ScopeQueryExecutionGuard.TryGetCurrent(out var scopeLease))
        {
            maxResponseRows = scopeLease.ClampRows(maxResponseRows);
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

