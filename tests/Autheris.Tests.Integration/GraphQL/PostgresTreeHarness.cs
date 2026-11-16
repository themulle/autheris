namespace Autheris.Tests.Integration.GraphQL;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.GraphQL;
using Npgsql;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// PostgreSQL harness for <see cref="TreeSecuredSourceBuilder"/> and <see cref="FastSqlEngine"/> row-parity tests.
/// Consent rows of tenant <c>t2</c> deliberately point at orders and items of tenant <c>t1</c>.
/// </summary>
internal sealed class PostgresTreeHarness
{
    public const string DataSource = "shop";

    public static readonly TableIdentifier OrdersId = new("shop", "public", "orders");
    public static readonly TableIdentifier ItemsId = new("shop", "public", "items");
    public static readonly TableIdentifier ConsentsId = new("shop", "public", "consents");
    public static readonly TableIdentifier ItemConsentsId = new("shop", "public", "item_consents");

    private static readonly PolicyPredicateRenderer Renderer = new();
    private readonly PostgreSqlCompiledSqlBinder _binder = new();

    public PostgresTreeHarness(
        string connectionString,
        GatewayOptions? options = null,
        Func<TableIdentifier, TableMetadata?>? dependentMetadata = null,
        IEnumerable<string>? catalogTables = null)
    {
        ConnectionString = connectionString;
        var metadataLookup = dependentMetadata ?? (id => DependentMetadata(id));
        var names = catalogTables?.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(["orders", "consents", "items", "item_consents"], StringComparer.OrdinalIgnoreCase);
        var entries = names.Select(Entry).ToList();

        CatalogProvider = new InMemoryCatalogProvider(new InMemoryTableCatalog(entries, "public"));
        MetadataRepository = new FuncMetadataRepository(metadataLookup);
        Factory = new GovernancePolicyFactory(CatalogProvider, MetadataRepository, Microsoft.Extensions.Options.Options.Create(options ?? new GatewayOptions()));
        Builder = new TreeSecuredSourceBuilder(Factory, Engine);
    }

    public string ConnectionString { get; }
    public IGovernedCatalogProvider CatalogProvider { get; }
    public ITableMetadataRepository MetadataRepository { get; }
    public FastSqlEngine Engine { get; } = new();
    public GovernancePolicyFactory Factory { get; }
    public TreeSecuredSourceBuilder Builder { get; }

    public static async Task SeedAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE public.orders (id integer PRIMARY KEY, email text, amount integer, tenant_id text);
            INSERT INTO public.orders VALUES
                (1, 'a@x.example', 5, 't1'),
                (2, 'b@x.example', 10, 't1'),
                (3, 'c@x.example', 20, 't1'),
                (4, 'd@x.example', 30, 't2'),
                (5, 'e@x.example', 40, 't2');
            CREATE TABLE public.consents (id integer PRIMARY KEY, order_id integer, purpose text, tenant_id text);
            INSERT INTO public.consents VALUES
                (1, 1, 'analytics', 't1'),
                (2, 3, 'marketing', 't1'),
                (3, 4, 'analytics', 't2'),
                (4, 2, 'analytics', 't2');
            CREATE TABLE public.items (id integer PRIMARY KEY, order_id integer, label text, tenant_id text);
            INSERT INTO public.items VALUES
                (1, 1, 'x', 't1'),
                (2, 1, 'y', 't1'),
                (3, 2, 'z', 't1'),
                (4, 4, 'w', 't2');
            CREATE TABLE public.item_consents (id integer PRIMARY KEY, item_id integer, tenant_id text);
            INSERT INTO public.item_consents VALUES
                (1, 1, 't1'),
                (2, 3, 't1'),
                (3, 4, 't2'),
                (4, 2, 't2');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public static TableCatalogEntry Entry(string table) => new(
        new TableIdentity("public", table),
        table switch
        {
            "orders" => [new CatalogColumn("id", "integer"), new CatalogColumn("email", "text"), new CatalogColumn("amount", "integer"), new CatalogColumn("tenant_id", "text")],
            "consents" => [new CatalogColumn("id", "integer"), new CatalogColumn("order_id", "integer"), new CatalogColumn("purpose", "text"), new CatalogColumn("tenant_id", "text")],
            "items" => [new CatalogColumn("id", "integer"), new CatalogColumn("order_id", "integer"), new CatalogColumn("label", "text"), new CatalogColumn("tenant_id", "text")],
            _ => [new CatalogColumn("id", "integer"), new CatalogColumn("item_id", "integer"), new CatalogColumn("tenant_id", "text")]
        },
        "tenant_id",
        1);

    public static TableMetadata DependentMetadata(TableIdentifier id, bool withTenant = true, bool active = true) => new()
    {
        Table = new Table { SourceName = DataSource, SchemaName = id.Schema, TableName = id.TableName, IsActive = active, SourceType = "PostgreSql" },
        Identifier = id,
        Columns = withTenant
            ? [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "tenant_id" }]
            : [new TableColumn { ColumnName = "id" }]
    };

    public static TableMetadata Meta(TableIdentifier id, bool maskEmail = false)
    {
        string[] columns = id.TableName switch
        {
            "orders" => ["id", "email", "amount", "tenant_id"],
            "items" => ["id", "order_id", "label", "tenant_id"],
            _ => ["id", "tenant_id"]
        };
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceName = DataSource, SchemaName = "public", TableName = id.TableName, SourceType = "PostgreSql" },
            PrimaryKeyColumns = ["id"],
            Columns = [.. columns.Select(c => new TableColumn { ColumnName = c, DataType = c is "id" or "amount" or "order_id" ? "integer" : "text" })],
            ColumnMaskingRules = maskEmail && id.TableName == "orders"
                ? new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" } }
                : new Dictionary<string, MaskingRule>()
        };
    }

    private static SqlIdentifier Id(string name) => new(name, true);
    private static ColumnReference Col(string alias, string column) => new(new SqlQualifiedName([Id(alias), Id(column)]));
    private static NamedTableSource Source(string table, string alias, string schema = "public") => new(new SqlQualifiedName([Id(schema), Id(table)]), Id(alias));

    private static SelectStatement Select(string table, string alias, string column, Expression where, string schema = "public") => new(
        null,
        new QuerySpecification(false, [new ColumnSelectItem(Col(alias, column), null)], Source(table, alias, schema), where, null, null),
        null,
        null);

    public static IRowPolicy ExistsPolicy(string purpose = "analytics", string schema = "public")
    {
        var namer = new PolicyParameterNamer("__rf_");
        var parameter = namer.Add(new PolicyValue(purpose, SqlParameterType.String));
        var where = new BinaryExpression(
            new BinaryExpression(Col("c", "order_id"), BinaryOperator.Equal, PolicyParameterNamer.Column($"{RowFilterAliases.Target}.id")),
            BinaryOperator.And,
            new BinaryExpression(Col("c", "purpose"), BinaryOperator.Equal, parameter));
        return new PolicyPredicateRowPolicy(
            PolicyPredicate.Create(new ExistsExpression(Select("consents", "c", "order_id", where, schema)), namer.Values), TargetSqlDialect.PostgreSql, Renderer);
    }

    public static IRowPolicy InPolicy(string purpose = "analytics", string schema = "public")
    {
        var namer = new PolicyParameterNamer("__rf_");
        var parameter = namer.Add(new PolicyValue(purpose, SqlParameterType.String));
        var where = new BinaryExpression(Col("c", "purpose"), BinaryOperator.Equal, parameter);
        return new PolicyPredicateRowPolicy(
            PolicyPredicate.Create(
                new InSubqueryExpression(PolicyParameterNamer.Column($"{RowFilterAliases.Target}.id"), Select("consents", "c", "order_id", where, schema), false),
                namer.Values),
            TargetSqlDialect.PostgreSql,
            Renderer);
    }

    public static IRowPolicy ItemPolicy(string schema = "public")
    {
        var where = new BinaryExpression(Col("ic", "item_id"), BinaryOperator.Equal, PolicyParameterNamer.Column($"{RowFilterAliases.Target}.id"));
        return new PolicyPredicateRowPolicy(
            PolicyPredicate.Create(new ExistsExpression(Select("item_consents", "ic", "item_id", where, schema)), new Dictionary<string, PolicyValue>()),
            TargetSqlDialect.PostgreSql,
            Renderer);
    }

    public static TableAccessDecision Decision(
        TableIdentifier table, IRowPolicy? policy, bool maskEmail = false, IReadOnlyDictionary<string, ColumnAccessLevel>? extraAccess = null)
    {
        var access = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["tenant_id"] = ColumnAccessLevel.Deny };
        if (maskEmail)
        {
            access["email"] = ColumnAccessLevel.Mask;
        }

        foreach (var (column, level) in extraAccess ?? new Dictionary<string, ColumnAccessLevel>())
        {
            access[column] = level;
        }

        var decision = TypedTreeRowPolicy.Allowed(table, access, policy);
        return maskEmail
            ? decision with
            {
                ColumnMasks = new ColumnMaskPolicy(new Dictionary<string, MaskSpec>
                {
                    ["email"] = SqlDataMaskingProvider.BuildUnkeyedMaskSpec(null, "email", DatabaseDialect.PostgreSql)
                })
            }
            : decision;
    }

    public static ResolvedTableAccess Resolved(TableMetadata metadata, TableAccessDecision decision, string tenant) =>
        new(metadata, decision, new TenantId(tenant), new Sid("user"), new ClaimsPrincipal());

    public async Task<TreeTableAccess> TreeAccessAsync(
        TableIdentifier table, IRowPolicy? policy, string tenant, bool maskEmail = false, IReadOnlyDictionary<string, ColumnAccessLevel>? extraAccess = null)
    {
        var meta = Meta(table, maskEmail);
        var decision = Decision(table, policy, maskEmail, extraAccess);
        TreeSecuredSource? source = null;
        if (TreeSqlCompiler.RowPolicyRequiresEnginePipeline(decision))
        {
            source = await Builder.BuildAsync(Resolved(meta, decision, tenant), DataSource, DatabaseDialect.PostgreSql, CancellationToken.None);
        }

        return new TreeTableAccess(meta, decision, "tenant_id", tenant, source);
    }

    public async Task<string> RunTreeAsync(TreeQueryNode root, IReadOnlyDictionary<TableIdentifier, TreeTableAccess> access)
    {
        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.PostgreSql);
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = compiled.Sql;
        foreach (var (name, value) in compiled.Parameters)
        {
            cmd.Parameters.AddWithValue(name.TrimStart('@'), value ?? DBNull.Value);
        }

        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<List<long>> TreeOrderIdsAsync(IRowPolicy policy, string tenant)
    {
        var access = new Dictionary<TableIdentifier, TreeTableAccess> { [OrdersId] = await TreeAccessAsync(OrdersId, policy, tenant) };
        var root = new TreeQueryNode(OrdersId, ["id", "email"]) { OrderBy = [new TreeOrder("id")], Limit = 100 };
        using var doc = JsonDocument.Parse(await RunTreeAsync(root, access));
        return [.. doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetInt64())];
    }

    public Task<List<long>> CompileOrderIdsAsync(IRowPolicy policy, string tenant) => CompileIdsAsync(OrdersId, policy, tenant);

    public Task<List<long>> CompileItemIdsAsync(IRowPolicy policy, string tenant) => CompileIdsAsync(ItemsId, policy, tenant);

    private async Task<List<long>> CompileIdsAsync(TableIdentifier table, IRowPolicy policy, string tenant)
    {
        var meta = Meta(table);
        var identity = new TableIdentity("public", table.TableName);
        var input = new GovernedPolicyInput(
            DataSource,
            new TenantId(tenant),
            new Dictionary<TableIdentity, TableAccessDecision> { [identity] = Decision(table, policy) },
            new HashSet<TableIdentity>(),
            GovernedSubqueryStrategy.Correlated)
        {
            Metadata = new Dictionary<TableIdentity, TableMetadata> { [identity] = meta }
        };
        var governed = await Factory.CreateAsync(input, CancellationToken.None);
        var request = CompileRequestFactory.Create(governed, TargetSqlDialect.PostgreSql, new WebSqlOptions(), false, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var compiled = Engine.Compile($"SELECT id FROM public.{table.TableName} ORDER BY id".AsMemory(), request, CancellationToken.None);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<long>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private sealed class InMemoryCatalogProvider(ITableCatalog catalog) : IGovernedCatalogProvider
    {
        public ValueTask<ITableCatalog> GetCatalogAsync(GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct) =>
            new(catalog);
    }

    private sealed class FuncMetadataRepository(Func<TableIdentifier, TableMetadata?> lookup) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(lookup(table));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }
}
