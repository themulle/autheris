using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Analytics;
using Autheris.Application.Common;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Catalog;
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
    private Timer? _refreshTimer;
    private sealed record BuiltState(string? Fingerprint, IReadOnlyList<CatalogOmittedTable> OmittedTables);

    // One volatile reference so readers never observe a fingerprint paired with another build's omissions.
    private volatile BuiltState _built = new(null, []);
    private volatile string? _builtVersion;
    private int _checking;

    public CatalogGraphQlTypeModule(
        ITableMetadataRepository metadataRepo,
        ITableRelationRepository relationRepo,
        Microsoft.Extensions.Logging.ILogger<CatalogGraphQlTypeModule>? logger = null,
        TimeSpan? refreshInterval = null,
        Autheris.Application.Catalog.ICatalogVersionProvider? versionProvider = null,
        bool aggregationEnabled = true,
        bool crossSourceEnabled = false,
        bool metricsEnabled = true)
    {
        _aggregationEnabled = aggregationEnabled;
        _crossSourceEnabled = crossSourceEnabled;
        _metricsEnabled = metricsEnabled;
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _relationRepo = relationRepo ?? throw new ArgumentNullException(nameof(relationRepo));
        _logger = logger;
        _refreshInterval = refreshInterval ?? TimeSpan.Zero;
        _versionProvider = versionProvider;
    }

    public event EventHandler<EventArgs>? TypesChanged;

    internal string? BuiltFingerprint => _built.Fingerprint;

    /// <summary>BUG #157: omissions of the schema actually served; an immutable snapshot swapped after each build.</summary>
    public IReadOnlyList<CatalogOmittedTable> OmittedTables => _built.OmittedTables;

    internal async Task<bool> CheckForCatalogChangesAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return false;
        }

        try
        {
            if (_versionProvider != null && _builtVersion != null)
            {
                try
                {
                    var currentVersion = await _versionProvider.GetVersionAsync(ct).ConfigureAwait(false);
                    if (string.Equals(currentVersion, _builtVersion, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "GraphQL catalog version check failed; falling back to schema fingerprint check.");
                }
            }

            string current;
            try
            {
                current = (await CatalogSchemaModel.BuildAsync(_metadataRepo, _relationRepo, null, dialectRegistry: null, crossSourceEnabled: _crossSourceEnabled, ct).ConfigureAwait(false)).Fingerprint();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "GraphQL catalog change check failed; retrying with the next interval.");
                return false;
            }

            if (string.Equals(current, _built.Fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            _logger?.LogInformation("GraphQL catalog changed; rebuilding the GraphQL schema.");
            TypesChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
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
            _refreshTimer ??= new Timer(_ => _ = CheckForCatalogChangesAsync(), null, _refreshInterval, _refreshInterval);
        }
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
        try
        {
            schemaModel = await CatalogSchemaModel.BuildAsync(_metadataRepo, _relationRepo, _logger, dialectRegistry: null, crossSourceEnabled: _crossSourceEnabled, cancellationToken)
                .ConfigureAwait(false);
            _built = new BuiltState(schemaModel.Fingerprint(), schemaModel.OmittedTables);
            if (_versionProvider != null)
            {
                _builtVersion = await _versionProvider.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "GraphQL catalog schema could not be built; serving the schema without catalog tables until the catalog is reachable.");
            schemaModel = CatalogSchemaModel.Empty;
            _built = new BuiltState(null, []);
            _builtVersion = null;
        }

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
        if (_metricsEnabled)
        {
            MetricGraphQlTypes.Register(types);
        }

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

                foreach (var col in table.Columns)
                {
                    var scalarName = GetScalarName(col.FieldType);
                    ITypeNode typeNode = col.IsNullable
                        ? new NamedTypeNode(scalarName)
                        : new NonNullTypeNode(new NamedTypeNode(scalarName));

                    d.Field(col.FieldName)
                        .Type(typeNode)
                        .Resolve(ctx => ResolveColumnValue(ctx, col));
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

                foreach (var table in schemaModel.Tables)
                {
                    var pk = table.Metadata.PrimaryKeyColumns.Count > 0 ? table.Metadata.PrimaryKeyColumns[0] : "id";
                    if (string.IsNullOrWhiteSpace(pk) || !System.Text.RegularExpressions.Regex.IsMatch(pk, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                    {
                        pk = "id";
                    }
                    d.Field(table.QueryFieldName)
                        .Description($"Query {table.TypeName}. Keyset paging can be performed via where: {{ {pk}: {{ gt: $last{pk} }} }}, orderBy: [{{ {pk}: ASC }}]. Offset pagination is supported up to offset 10000.")
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
                d.Field("in").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(scalar))));
                d.Field("nin").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(scalar))));
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
                : (int.TryParse(prop.GetString(), System.Globalization.NumberStyles.Integer, inv, out var strN) ? strN : ReportMasked(ctx, col)),
            CatalogFieldType.Long => prop.ValueKind == JsonValueKind.Number
                ? (prop.TryGetInt64(out var l) ? l : (long.TryParse(prop.GetRawText(), System.Globalization.NumberStyles.Integer, inv, out var pl) ? pl : ReportMasked(ctx, col)))
                : (long.TryParse(prop.GetString(), System.Globalization.NumberStyles.Integer, inv, out var strL) ? strL : ReportMasked(ctx, col)),
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
            CatalogFieldType.String or CatalogFieldType.DateTime => prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.GetRawText(),
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
            var queryNode = GraphQlTreeBuilder.BuildTree(ctx, table, schema, maxResponseRows);
            using var doc = await treeService.ExecuteAsync(principal, queryNode, headers, opId, ctx.RequestAborted)
                .ConfigureAwait(false);

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
        catch (GatewayInvalidQueryException ex)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage(ex.Message).SetCode("INVALID_QUERY").Build());
        }
        catch (TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RequiredFilterColumnMissing))
                .SetCode(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RequiredFilterColumnMissing)
                .Build());
        }
        catch (Autheris.Application.Data.Relations.CrossSourceRelationException ex)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage(ex.Message).SetCode(ex.ErrorCode).Build());
        }
        catch (GatewayForbiddenException)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage("Access denied.").SetCode("ACCESS_DENIED").Build());
        }
        catch (GatewayThrottledException ex)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage(ex.Message).SetCode("TOO_MANY_REQUESTS").Build());
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
}

