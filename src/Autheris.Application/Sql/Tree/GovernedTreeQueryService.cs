using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autheris.Application.Data.Relations;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree.Federation;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Sql.Tree;

public interface IGovernedTreeQueryService
{
    /// <summary>
    /// Runs a GraphQL selection tree as one governed statement and returns the JSON array of root rows.
    /// The caller owns (disposes) the document.
    /// </summary>
    Task<JsonDocument> ExecuteAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        string operationId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists the columns of <paramref name="root"/> that are masked for the caller, using the access decisions memoized
    /// by a preceding <see cref="ExecuteAsync"/> of the same operation. Empty when nothing is masked or no decision exists.
    /// </summary>
    IReadOnlyList<TreeMaskedColumn> GetMaskedColumns(ClaimsPrincipal? principal, TreeQueryNode root, string operationId);

    /// <summary>
    /// Cleans up any cached table access decisions, locks, and audit tracking for the given GraphQL operation.
    /// </summary>
    void ClearOperation(string operationId);
}

/// <summary>
/// G2/G3/G5 (docs/plans/rls-subquery-in-strategy.md): governed execution of a GraphQL selection tree.
/// <list type="bullet">
/// <item>Access per table is resolved once per operation through <see cref="ITableAccessResolver"/>,
/// the same decision path as OData and the table root field; one audit entry per table and operation.</item>
/// <item>All tables must live in one SQL data source whose provider matches the catalog dialect (D-1).</item>
/// <item>The tree runs as ONE statement (<see cref="TreeSqlCompiler"/>); the database builds the JSON, the gateway reads
/// one text value with a size limit, parses it once and only rewrites HMAC columns.</item>
/// </list>
/// </summary>
public sealed class GovernedTreeQueryService : IGovernedTreeQueryService, IDisposable
{
    private sealed class OperationMemo : IDisposable
    {
        public System.Collections.Concurrent.ConcurrentDictionary<(TableIdentifier Table, string Tenant, string UserSid), ResolvedTableAccess> AccessByTable { get; } = new();
        public System.Collections.Concurrent.ConcurrentDictionary<(TableIdentifier Table, string Tenant, string UserSid, bool Allowed), byte> Audited { get; } = new();
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;

        public void Dispose()
        {
            Lock.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var memo in _memos.Values)
        {
            memo.Dispose();
        }
        _memos.Clear();
    }

    private readonly ITableAccessResolver _accessResolver;
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IAuditLogRepository _auditLog;
    private readonly Autheris.Application.Audit.IAuditWriter _auditWriter;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly GatewayOptions _options;
    private readonly ITableReadConcurrencyGate? _concurrencyGate;
    private readonly ILogger<GovernedTreeQueryService>? _logger;
    private readonly IDbSessionContextInitializer _sessionInitializer;
    private readonly ICrossSourceRelationResolver? _crossSourceResolver;
    private readonly ITableRelationRepository? _relationRepository;

    // G3, G4, G7, D7 & R-GQL-3: memo and audit are scoped per operationId, tenant, and user to isolate operations and prevent cross-tenant/cross-principal leak.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OperationMemo> _memos = new();

    private const int ThrottledRetryAfterSeconds = 2;

    public GovernedTreeQueryService(
        ITableAccessResolver accessResolver,
        ISqlConnectionFactory connectionFactory,
        IAuditLogRepository auditLog,
        IColumnMaskingProvider maskingProvider,
        IOptions<GatewayOptions> options,
        ITableReadConcurrencyGate? concurrencyGate = null,
        ILogger<GovernedTreeQueryService>? logger = null,
        IDbSessionContextInitializer? sessionInitializer = null,
        Autheris.Application.Audit.IAuditWriter? auditWriter = null,
        ICrossSourceRelationResolver? crossSourceResolver = null,
        ITableRelationRepository? relationRepository = null)
    {
        _accessResolver = accessResolver ?? throw new ArgumentNullException(nameof(accessResolver));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _auditWriter = auditWriter ?? new Autheris.Application.Audit.AuditWriter(_auditLog, Microsoft.Extensions.Logging.Abstractions.NullLogger<Autheris.Application.Audit.AuditWriter>.Instance);
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _concurrencyGate = concurrencyGate;
        _logger = logger;
        _sessionInitializer = sessionInitializer ?? new DbSessionContextInitializer();
        _crossSourceResolver = crossSourceResolver;
        _relationRepository = relationRepository;
    }

    public IReadOnlyList<TreeMaskedColumn> GetMaskedColumns(ClaimsPrincipal? principal, TreeQueryNode root, string operationId)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return [];
        }

        var tenant = principal.GetTenantId().Value;
        var userSid = principal.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            userSid = "anonymous";
        }

        if (!_memos.TryGetValue($"{operationId}|{tenant}|{userSid}", out var memo))
        {
            return [];
        }

        var columnsByTable = new Dictionary<TableIdentifier, List<string>>();
        CollectColumns(root, columnsByTable);

        var treeAccess = new Dictionary<TableIdentifier, TreeTableAccess>();
        foreach (var table in columnsByTable.Keys)
        {
            if (!memo.AccessByTable.TryGetValue((table, tenant, userSid), out var access) || !access.Decision.IsAllowed)
            {
                return [];
            }
            treeAccess[table] = new TreeTableAccess(access.Metadata, access.Decision);
        }

        return TreeSqlCompiler.CollectMaskedColumns(root, treeAccess, _options.IsColumnMaskingDisabled);
    }

    public void ClearOperation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        var prefix = operationId + "|";
        foreach (var key in _memos.Keys)
        {
            if (string.Equals(key, operationId, StringComparison.Ordinal) ||
                key.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (_memos.TryRemove(key, out var memo))
                {
                    memo.Dispose();
                }
            }
        }
    }

    private void EvictStaleMemosIfNecessary()
    {
        if (_memos.Count <= 50)
        {
            return;
        }

        var threshold = DateTime.UtcNow.AddMinutes(-5);
        foreach (var kvp in _memos)
        {
            if (kvp.Value.CreatedAtUtc < threshold && _memos.TryRemove(kvp.Key, out var stale))
            {
                stale.Dispose();
            }
        }

        // Hard capacity cap to defend against exhaustion attacks
        if (_memos.Count > 200)
        {
            var oldestKeys = _memos
                .OrderBy(kvp => kvp.Value.CreatedAtUtc)
                .Take(_memos.Count - 200)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in oldestKeys)
            {
                if (_memos.TryRemove(key, out var oldest))
                {
                    oldest.Dispose();
                }
            }
        }
    }

    public async Task<JsonDocument> ExecuteAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        string operationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        EvictStaleMemosIfNecessary();

        var tenant = principal.GetTenantId().Value;
        var userSid = principal.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            userSid = "anonymous";
        }

        var memoKey = $"{operationId}|{tenant}|{userSid}";
        var memo = _memos.GetOrAdd(memoKey, _ => new OperationMemo());

        var maxRows = _options.GraphQL?.MaxResponseRows > 0 ? _options.GraphQL.MaxResponseRows : 1000;
        if (root.Limit > maxRows)
        {
            throw new GatewayInvalidQueryException($"The page size must not exceed {maxRows}.");
        }

        var maxOffset = _options.GraphQL?.MaxAllowedOffset >= 0 ? _options.GraphQL.MaxAllowedOffset : 10000;
        if (root.Offset < 0)
        {
            throw new GatewayInvalidQueryException("The offset cannot be negative.");
        }
        if (root.Offset > maxOffset)
        {
            throw new GatewayInvalidQueryException($"The offset cannot exceed {maxOffset}.");
        }

        var maxBudget = _options.GraphQL?.MaxAggregateRowBudget > 0 ? _options.GraphQL.MaxAggregateRowBudget : 50000;
        ValidateTreeBudget(root, maxBudget);

        // 1. Access per table (memoized per operation), denied tables fail before any database access.
        var columnsByTable = new Dictionary<TableIdentifier, List<string>>();
        CollectColumns(root, columnsByTable);

        var resolved = new Dictionary<TableIdentifier, ResolvedTableAccess>();
        foreach (var (table, columns) in columnsByTable)
        {
            var access = await ResolveOnceAsync(memo, principal, table, columns, requestHeaders, ct).ConfigureAwait(false);
            await AuditOnceAsync(memo, access, table, ct).ConfigureAwait(false);
            if (!access.Decision.IsAllowed)
            {
                throw new GatewayForbiddenException("Access denied.");
            }
            resolved[table] = access;
        }

        // 2. One SQL data source, provider dialect == catalog dialect. The source-name rule is shared with the schema
        //    (RelationExecutability.IsSameExecutionSource); the type and dialect checks stay separate here so that each
        //    failure keeps its own exception and message. Together they are never more permissive than the schema predicate.
        var rootAccess = resolved[root.Table];
        var sourceName = rootAccess.Metadata.Table.SourceName;

        // Partition root relations into single-source vs cross-source
        var sameSourceRelations = new List<TreeRelationNode>();
        var crossSourceRelations = new List<TreeRelationNode>();

        foreach (var rel in root.Relations)
        {
            var childAccess = resolved[rel.Child.Table];
            if (RelationExecutability.HasSameSourceName(rootAccess.Metadata, childAccess.Metadata))
            {
                sameSourceRelations.Add(rel);
            }
            else
            {
                crossSourceRelations.Add(rel);
            }
        }

        if (crossSourceRelations.Count > 0)
        {
            if (_crossSourceResolver == null)
            {
                throw new GatewayInvalidQueryException("Relations across data sources are not supported in one query.");
            }

            // SEC-DATA75-02: Master federation opt-in check
            if (!_options.IsCrossSourceEnabled)
            {
                throw new CrossSourceRelationException(
                    CrossSourceRelationErrorCodes.CrossSourceRelationNotEnabled,
                    "Cross-source relations are disabled by configuration.");
            }

            foreach (var cr in crossSourceRelations)
            {
                var childAccess = resolved[cr.Child.Table];

                // SEC-DATA75-01: Clear keys check on parent join keys
                foreach (var parentCol in cr.ParentColumns)
                {
                    var col = rootAccess.Metadata.GetColumn(parentCol);
                    if (col == null || rootAccess.Decision.GetEffectiveColumnAccess(col.ColumnName, rootAccess.Metadata) != ColumnAccessLevel.Clear)
                    {
                        throw new CrossSourceRelationException(
                            CrossSourceRelationErrorCodes.RelationKeyMasked,
                            $"Parent join key column '{parentCol}' on table '{root.Table}' must have Clear access for cross-source relation resolution.");
                    }
                }

                // SEC-DATA75-01: Clear keys check on child join keys
                foreach (var childCol in cr.ChildColumns)
                {
                    var col = childAccess.Metadata.GetColumn(childCol);
                    if (col == null || childAccess.Decision.GetEffectiveColumnAccess(col.ColumnName, childAccess.Metadata) != ColumnAccessLevel.Clear)
                    {
                        throw new CrossSourceRelationException(
                            CrossSourceRelationErrorCodes.RelationKeyMasked,
                            $"Target join key column '{childCol}' on table '{cr.Child.Table}' must have Clear access for cross-source relation resolution.");
                    }
                }

                // SEC-DATA75-02: Four-eyes consent check
                TableRelation? tableRel = null;
                if (_relationRepository != null)
                {
                    var parentRels = await _relationRepository.GetRelationsForTableAsync(root.Table, ct).ConfigureAwait(false);
                    tableRel = parentRels.FirstOrDefault(r =>
                        (r.ParentTableIdentifier.Equals(root.Table) && r.ChildTableIdentifier.Equals(cr.Child.Table) &&
                         r.JoinKeysParent.SequenceEqual(cr.ParentColumns) && r.JoinKeysChild.SequenceEqual(cr.ChildColumns)) ||
                        (r.ParentTableIdentifier.Equals(cr.Child.Table) && r.ChildTableIdentifier.Equals(root.Table) &&
                         r.JoinKeysParent.SequenceEqual(cr.ChildColumns) && r.JoinKeysChild.SequenceEqual(cr.ParentColumns)));

                    if (tableRel == null)
                    {
                        var childRels = await _relationRepository.GetRelationsForTableAsync(cr.Child.Table, ct).ConfigureAwait(false);
                        tableRel = childRels.FirstOrDefault(r =>
                            (r.ParentTableIdentifier.Equals(root.Table) && r.ChildTableIdentifier.Equals(cr.Child.Table) &&
                             r.JoinKeysParent.SequenceEqual(cr.ParentColumns) && r.JoinKeysChild.SequenceEqual(cr.ChildColumns)) ||
                            (r.ParentTableIdentifier.Equals(cr.Child.Table) && r.ChildTableIdentifier.Equals(root.Table) &&
                             r.JoinKeysParent.SequenceEqual(cr.ChildColumns) && r.JoinKeysChild.SequenceEqual(cr.ParentColumns)));
                    }
                }

                if (tableRel != null && !tableRel.AllowCrossDataSource)
                {
                    throw new CrossSourceRelationException(
                        CrossSourceRelationErrorCodes.CrossSourceRelationNotEnabled,
                        $"Relation '{tableRel.RelationName}' between '{root.Table}' and '{cr.Child.Table}' crosses data sources, but AllowCrossDataSource is false.");
                }
            }
        }

        // Collect all tables belonging to the primary single-source query
        var sameSourceTables = new HashSet<TableIdentifier> { root.Table };
        void CollectSameSourceTables(TreeRelationNode rel)
        {
            sameSourceTables.Add(rel.Child.Table);
            foreach (var r in rel.Child.Relations)
            {
                CollectSameSourceTables(r);
            }
        }
        foreach (var rel in sameSourceRelations)
        {
            CollectSameSourceTables(rel);
        }

        // Validate that all tables in the primary query belong to the same SQL data source
        foreach (var tableId in sameSourceTables)
        {
            var access = resolved[tableId];
            if (access.Metadata.DataSourceType != DataSourceType.Sql)
            {
                throw new GatewayInvalidQueryException($"The table '{access.Metadata.Identifier}' is not a SQL table and cannot be queried with relations.");
            }
            if (!RelationExecutability.HasSameSourceName(rootAccess.Metadata, access.Metadata))
            {
                throw new GatewayInvalidQueryException("Relations across data sources are not supported in one query.");
            }
        }

        var connections = _options.DataSources?.Connections;
        if (connections == null || !connections.TryGetValue(sourceName, out var connOptions) || string.IsNullOrWhiteSpace(connOptions.ConnectionString))
        {
            throw new InvalidOperationException($"The data source '{sourceName}' has no database connection.");
        }

        var provider = connOptions.Provider;
        if (!DataSourceProvider.TryResolveDialect(provider, out var dialect))
        {
            throw new InvalidOperationException($"The provider '{provider}' of data source '{sourceName}' is not supported.");
        }
        foreach (var tableId in sameSourceTables)
        {
            var access = resolved[tableId];
            if (access.Metadata.Dialect != dialect)
            {
                throw new InvalidOperationException($"Catalog dialect '{access.Metadata.Dialect}' of '{access.Metadata.Identifier}' does not match the provider '{provider}' of data source '{sourceName}'.");
            }
        }

        // 3. Compile primary query (stripping cross-source relations, appending join keys to parent Columns)
        var primaryColumns = new List<string>(root.Columns);
        foreach (var cr in crossSourceRelations)
        {
            foreach (var pCol in cr.ParentColumns)
            {
                if (!primaryColumns.Contains(pCol, StringComparer.OrdinalIgnoreCase))
                {
                    primaryColumns.Add(pCol);
                }
            }
        }

        var primaryRootNode = root with
        {
            Columns = primaryColumns,
            Relations = sameSourceRelations
        };

        var treeAccess = new Dictionary<TableIdentifier, TreeTableAccess>();
        foreach (var tableId in sameSourceTables)
        {
            var access = resolved[tableId];
            var tenantColumn = TableMetadata.RequireTenantColumnOrThrow(
                access.Metadata,
                _options.DataSources?.RequireTenantColumn == true,
                _options.DataSources?.TenantColumnExemptTables);
            treeAccess[tableId] = new TreeTableAccess(access.Metadata, access.Decision, tenantColumn, tenantColumn == null ? null : access.Tenant.Value);
        }

        var compiled = TreeSqlCompiler.Compile(primaryRootNode, treeAccess, dialect, _options.IsColumnMaskingDisabled);

        // 4. Bounded concurrency per user and root table, then one round trip.
        using var lease = AcquireLease(rootAccess);
        var json = await RunAsync(compiled, connOptions, dialect, rootAccess.Tenant, rootAccess.UserSid.Value, purpose: null, ct).ConfigureAwait(false);

        // Fast path: no cross-source relations and no HMAC columns
        if (crossSourceRelations.Count == 0 && compiled.HmacColumns.Count == 0)
        {
            return JsonDocument.Parse(json);
        }

        // Parse JSON for HMAC pseudonymization or cross-source merging
        var node = JsonNode.Parse(json) ?? new JsonArray();
        tenant = rootAccess.Tenant.Value;
        foreach (var hmac in compiled.HmacColumns)
        {
            var rule = MaskingRule.CreateTenantScopedHmacRule(hmac.Rule, tenant, _options.DataMasking?.HmacKeyId);
            Pseudonymize(node, hmac.Path, 0, hmac.Column, rule);
        }

        if (crossSourceRelations.Count > 0 && node is JsonArray rootArray)
        {
            // SEC-DATA75-03: Parameter-bounded batching & row cap limit
            if (rootArray.Count > maxRows)
            {
                throw new RowLimitExceededException($"Cross-source parent row count of {rootArray.Count} exceeds the maximum allowed limit of {maxRows}.");
            }

            if (rootArray.Count > 0)
            {
                if (_crossSourceResolver == null)
                {
                    throw new InvalidOperationException("ICrossSourceRelationResolver is not available for cross-source relation execution.");
                }

                var parentRowDicts = new List<IReadOnlyDictionary<string, object?>>(rootArray.Count);
                foreach (var item in rootArray)
                {
                    if (item is JsonObject obj)
                    {
                        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (propName, propVal) in obj)
                        {
                            dict[propName] = JsonNodeToObject(propVal);
                        }
                        parentRowDicts.Add(dict);
                    }
                    else
                    {
                        parentRowDicts.Add(new Dictionary<string, object?>());
                    }
                }

                foreach (var cr in crossSourceRelations)
                {
                    var childAccess = resolved[cr.Child.Table];
                    TableRelation? relModel = null;
                    if (_relationRepository != null)
                    {
                        var parentRels = await _relationRepository.GetRelationsForTableAsync(root.Table, ct).ConfigureAwait(false);
                        relModel = parentRels.FirstOrDefault(r =>
                            (r.ParentTableIdentifier.Equals(root.Table) && r.ChildTableIdentifier.Equals(cr.Child.Table) &&
                             r.JoinKeysParent.SequenceEqual(cr.ParentColumns) && r.JoinKeysChild.SequenceEqual(cr.ChildColumns)) ||
                            (r.ParentTableIdentifier.Equals(cr.Child.Table) && r.ChildTableIdentifier.Equals(root.Table) &&
                             r.JoinKeysParent.SequenceEqual(cr.ChildColumns) && r.JoinKeysChild.SequenceEqual(cr.ParentColumns)));
                    }

                    relModel ??= new TableRelation
                    {
                        ParentTableIdentifier = root.Table,
                        ChildTableIdentifier = cr.Child.Table,
                        RelationName = cr.ResponseKey,
                        JoinKeysParent = cr.ParentColumns,
                        JoinKeysChild = cr.ChildColumns,
                        Cardinality = cr.IsList ? RelationCardinality.OneToMany : RelationCardinality.ManyToOne,
                        AllowCrossDataSource = true
                    };

                    var crossRequest = new CrossSourceRelationRequest(
                        Relation: relModel,
                        ParentRows: parentRowDicts,
                        User: principal ?? new ClaimsPrincipal(),
                        TenantId: rootAccess.Tenant,
                        SelectColumns: cr.Child.Columns.Count > 0 ? cr.Child.Columns : null,
                        PerParentLimit: cr.Child.Limit,
                        ParentDecision: rootAccess.Decision);

                    var crossResult = await _crossSourceResolver.ResolveRelationAsync(crossRequest, ct).ConfigureAwait(false);

                    for (int i = 0; i < rootArray.Count; i++)
                    {
                        if (rootArray[i] is JsonObject parentObj)
                        {
                            if (cr.IsList)
                            {
                                var childArray = new JsonArray();
                                if (crossResult.OneToManyByParentIndex != null && i < crossResult.OneToManyByParentIndex.Count)
                                {
                                    foreach (var childRow in crossResult.OneToManyByParentIndex[i])
                                    {
                                        var childObj = new JsonObject();
                                        foreach (var (k, v) in childRow)
                                        {
                                            childObj[k] = ToJsonNode(v);
                                        }
                                        childArray.Add(childObj);
                                    }
                                }
                                parentObj[cr.ResponseKey] = childArray;
                            }
                            else
                            {
                                var singleChild = crossResult.ManyToOneByParentIndex != null && i < crossResult.ManyToOneByParentIndex.Count ? crossResult.ManyToOneByParentIndex[i] : null;
                                if (singleChild == null)
                                {
                                    parentObj[cr.ResponseKey] = null;
                                }
                                else
                                {
                                    var childObj = new JsonObject();
                                    foreach (var (k, v) in singleChild)
                                    {
                                        childObj[k] = ToJsonNode(v);
                                    }
                                    parentObj[cr.ResponseKey] = childObj;
                                }
                            }
                        }
                    }

                    // SEC-DATA75-05: Dedicated audit log event
                    var auditEntry = GovernedQueryStages.BuildEntry(
                        AuditEventTypes.CrossSourceRelationResolved,
                        rootAccess.Tenant,
                        rootAccess.UserSid,
                        target: cr.Child.Table.ToString(),
                        allowed: true,
                        details: new Dictionary<string, object?>
                        {
                            ["parent_table"] = root.Table.ToString(),
                            ["child_table"] = cr.Child.Table.ToString(),
                            ["parent_source"] = rootAccess.Metadata.Table.SourceName,
                            ["child_source"] = childAccess.Metadata.Table.SourceName,
                            ["relation"] = cr.ResponseKey,
                            ["rows_stitched"] = crossResult.TargetRowsMaterialized,
                            ["batches"] = crossResult.BatchesExecuted
                        });
                    await GovernedQueryStages.WriteAsync(_auditWriter, auditEntry, AuditTier.BestEffort, ct).ConfigureAwait(false);
                }
            }
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            node.WriteTo(writer);
        }
        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }

    private static void CollectColumns(TreeQueryNode node, Dictionary<TableIdentifier, List<string>> columnsByTable)
    {
        if (!columnsByTable.TryGetValue(node.Table, out var columns))
        {
            columns = [];
            columnsByTable[node.Table] = columns;
        }
        columns.AddRange(node.Columns.Where(c => !columns.Contains(c, StringComparer.OrdinalIgnoreCase)));
        foreach (var relation in node.Relations)
        {
            CollectColumns(relation.Child, columnsByTable);
        }
    }

    private async Task<ResolvedTableAccess> ResolveOnceAsync(
        OperationMemo memo,
        ClaimsPrincipal? principal,
        TableIdentifier table,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        CancellationToken ct)
    {
        var tenant = principal.GetTenantId().Value;
        var userSid = principal.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            userSid = "anonymous";
        }
        var key = (table, tenant, userSid);

        if (memo.AccessByTable.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await memo.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (memo.AccessByTable.TryGetValue(key, out cached))
            {
                return cached;
            }
            var access = await _accessResolver.ResolveTableAccessAsync(principal, table, columns, requestHeaders, ct).ConfigureAwait(false);
            memo.AccessByTable[key] = access;
            return access;
        }
        finally
        {
            memo.Lock.Release();
        }
    }

    private async Task AuditOnceAsync(OperationMemo memo, ResolvedTableAccess access, TableIdentifier table, CancellationToken ct)
    {
        var tenant = access.Tenant.Value;
        var userSid = string.IsNullOrWhiteSpace(access.UserSid.Value) ? "anonymous" : access.UserSid.Value;
        var auditKey = (table, tenant, userSid, access.Decision.IsAllowed);

        if (!memo.Audited.TryAdd(auditKey, 1))
        {
            return;
        }

        await GovernedQueryStages.RecordTableReadAsync(
            _auditWriter, access.Tenant, access.UserSid, table, access.Decision.IsAllowed,
            GovernedQueryStages.DecisionDetails(access.Decision, path: "graphql_tree"), ct).ConfigureAwait(false);
    }

    private IDisposable? AcquireLease(ResolvedTableAccess rootAccess)
    {
        var max = _options.DataSources?.MaxConcurrentReadsPerUserAndTable ?? 0;
        if (_concurrencyGate == null || max <= 0)
        {
            return null;
        }
        var key = $"{rootAccess.Tenant.Value}|{rootAccess.UserSid.Value}|{rootAccess.Metadata.Identifier.ToQualifiedName()}".ToLowerInvariant();
        return _concurrencyGate.TryEnter(key, max) ?? throw new GatewayThrottledException(ThrottledRetryAfterSeconds);
    }

    private async Task<string> RunAsync(
        CompiledTreeQuery compiled,
        DataSourceConnectionOptions connOptions,
        DatabaseDialect dialect,
        TenantId tenant,
        string? userSid,
        string? purpose,
        CancellationToken ct)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);
        DbTransaction? tx = null;
        try
        {
            tx = await _sessionInitializer.InitializeSessionAsync(
                connection,
                dialect,
                tenant,
                userSid: userSid,
                purpose: purpose,
                requireTransaction: true,
                ct: ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = compiled.Sql;
            command.CommandTimeout = Math.Max(1, connOptions.CommandTimeoutSeconds);
            foreach (var (name, value) in compiled.Parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = ToProviderValue(value, dialect) ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            if (_options.Logging?.LogGeneratedSql == true)
            {
                _logger?.LogDebug("GovernedTreeQueryService: Generated SQL: {Sql}", compiled.Sql);
            }

            string json;
            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, ct).ConfigureAwait(false))
            {
                json = await reader.ReadAsync(ct).ConfigureAwait(false) && !await reader.IsDBNullAsync(0, ct).ConfigureAwait(false)
                    ? await ReadBoundedTextAsync(reader, MaxResponseBytes(), ct).ConfigureAwait(false)
                    : "[]";
            }

            if (tx != null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            return json;
        }
        catch (Exception)
        {
            if (tx != null)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogWarning(rbEx, "Failed to rollback transaction after error in GovernedTreeQueryService");
                }
            }
            throw;
        }
        finally
        {
            if (tx != null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private long MaxResponseBytes() =>
        _options.GraphQL?.MaxResponseBytes > 0 ? _options.GraphQL.MaxResponseBytes : 10 * 1024 * 1024;

    /// <summary>O11: reads the JSON text in chunks and stops at the byte limit (UTF-16 estimate, as elsewhere).</summary>
    private static async Task<string> ReadBoundedTextAsync(DbDataReader reader, long maxBytes, CancellationToken ct)
    {
        using var textReader = reader.GetTextReader(0);
        var builder = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await textReader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            builder.Append(buffer, 0, read);
            if (builder.Length * 2L > maxBytes)
            {
                throw new GatewaySecurityException($"Response size exceeds the configured limit of {maxBytes} bytes.", "RESPONSE_TOO_LARGE");
            }
        }
        return builder.ToString();
    }

    private void Pseudonymize(JsonNode? node, IReadOnlyList<string> path, int index, string column, MaskingRule rule)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    Pseudonymize(item, path, index, column, rule);
                }
                break;
            case JsonObject obj when index < path.Count:
                Pseudonymize(obj[path[index]], path, index + 1, column, rule);
                break;
            case JsonObject obj:
                if (obj.TryGetPropertyValue(column, out var value) && value != null)
                {
                    // SQL2-12: same canonical input as the REST paths, so a value gets the same pseudonym on every API.
                    var raw = Autheris.Application.Services.MaskingInputCanonicalizer.FromJson(value);
                    var masked = _maskingProvider.MaskValue(column, raw, rule);
                    obj[column] = masked == null ? null : JsonValue.Create(Convert.ToString(masked, System.Globalization.CultureInfo.InvariantCulture));
                }
                break;
        }
    }

    private static void ValidateTreeBudget(TreeQueryNode root, int maxBudget)
    {
        long estimatedRows = EstimateRows(root, 1, isList: true);
        if (estimatedRows > maxBudget)
        {
            throw new GatewayInvalidQueryException($"The query exceeds the aggregate row budget of {maxBudget} across nested relations (estimated worst-case rows: {estimatedRows}).");
        }
    }

    private static long EstimateRows(TreeQueryNode node, long parentMultiplier, bool isList = true)
    {
        long currentRows = isList ? parentMultiplier * Math.Max(1, node.Limit) : parentMultiplier;
        long total = currentRows;
        foreach (var rel in node.Relations)
        {
            total += EstimateRows(rel.Child, currentRows, rel.IsList);
        }
        return total;
    }

    /// <summary>
    /// R-GQL-10: Microsoft.Data.Sqlite binds DateTimeOffset as "yyyy-MM-dd HH:mm:ss+00:00" (space). ISO-8601 text values
    /// ("...T...Z") then compare wrongly, so SQLite receives the UTC value in ISO-8601 form; other providers bind natively.
    /// </summary>
    internal static object? ToProviderValue(object? value, DatabaseDialect dialect) =>
        dialect == DatabaseDialect.Sqlite && value is DateTimeOffset dto
            ? dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", System.Globalization.CultureInfo.InvariantCulture)
            : value;

    private static object? JsonNodeToObject(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<long>(out var l)) return l;
            if (val.TryGetValue<double>(out var d)) return d;
            if (val.TryGetValue<bool>(out var b)) return b;
            if (val.TryGetValue<string>(out var s)) return s;
            return val.ToString();
        }
        return node;
    }

    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        null or DBNull => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        decimal m => JsonValue.Create(m),
        DateTime dt => JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
    };
}
