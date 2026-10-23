using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using NSubstitute;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// SQLA-06 follow-up: a real SQLite database, the real engine, the real <see cref="GovernancePolicyFactory"/> and the real
/// <see cref="TreeSecuredSourceBuilder"/>. The tree runs through the builder, the WebSQL path through the same factory and engine, so the two
/// can be compared row by row. Consent rows of tenant t2 deliberately point at orders and items of tenant t1.
/// </summary>
internal sealed class SecuredTreeHarness : IDisposable
{
    public const string DataSource = "shop";

    public static readonly TableIdentifier OrdersId = new("shop", "main", "orders");
    public static readonly TableIdentifier ItemsId = new("shop", "main", "items");
    public static readonly TableIdentifier ConsentsId = new("shop", "main", "consents");
    public static readonly TableIdentifier ItemConsentsId = new("shop", "main", "item_consents");

    private static readonly PolicyPredicateRenderer Renderer = new();

    private readonly Func<TableIdentifier, TableMetadata?> _dependentMetadata;

    public SecuredTreeHarness(
        GatewayOptions? options = null,
        Func<TableIdentifier, TableMetadata?>? dependentMetadata = null,
        IEnumerable<string>? catalogTables = null)
    {
        Connection = new SqliteConnection("Data Source=:memory:");
        Connection.Open();
        using (var cmd = Connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE orders (id INTEGER PRIMARY KEY, email TEXT, amount INTEGER, tenant_id TEXT);
                INSERT INTO orders VALUES (1, 'a@x.example', 5, 't1'), (2, 'b@x.example', 10, 't1'), (3, 'c@x.example', 20, 't1'),
                                          (4, 'd@x.example', 30, 't2'), (5, 'e@x.example', 40, 't2');
                CREATE TABLE consents (id INTEGER PRIMARY KEY, order_id INTEGER, purpose TEXT, tenant_id TEXT);
                INSERT INTO consents VALUES (1, 1, 'analytics', 't1'), (2, 3, 'marketing', 't1'), (3, 4, 'analytics', 't2'), (4, 2, 'analytics', 't2');
                CREATE TABLE items (id INTEGER PRIMARY KEY, order_id INTEGER, label TEXT, tenant_id TEXT);
                INSERT INTO items VALUES (1, 1, 'x', 't1'), (2, 1, 'y', 't1'), (3, 2, 'z', 't1'), (4, 4, 'w', 't2');
                CREATE TABLE item_consents (id INTEGER PRIMARY KEY, item_id INTEGER, tenant_id TEXT);
                INSERT INTO item_consents VALUES (1, 1, 't1'), (2, 3, 't1'), (3, 4, 't2'), (4, 2, 't2');
                """;
            cmd.ExecuteNonQuery();
        }

        _dependentMetadata = dependentMetadata ?? (id => DependentMetadata(id));
        var names = catalogTables?.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(["orders", "consents", "items", "item_consents"], StringComparer.OrdinalIgnoreCase);
        var entries = new List<TableCatalogEntry>();
        foreach (var name in names)
        {
            entries.Add(Entry(name));
        }

        var provider = CatalogProvider = Substitute.For<IGovernedCatalogProvider>();
        provider.GetCatalogAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ITableCatalog>(new InMemoryTableCatalog(entries, "main")));
        var tables = MetadataRepository = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_dependentMetadata(call.Arg<TableIdentifier>())));
        Factory = new GovernancePolicyFactory(provider, tables, Microsoft.Extensions.Options.Options.Create(options ?? new GatewayOptions()));
        Builder = new TreeSecuredSourceBuilder(Factory, Engine);
    }

    public SqliteConnection Connection { get; }

    public IGovernedCatalogProvider CatalogProvider { get; }

    public ITableMetadataRepository MetadataRepository { get; }

    public FastSqlEngine Engine { get; } = new();

    public GovernancePolicyFactory Factory { get; }

    public TreeSecuredSourceBuilder Builder { get; }

    public void Dispose() => Connection.Dispose();

    public static TableCatalogEntry Entry(string table) => new(
        new TableIdentity("main", table),
        table switch
        {
            "orders" => ImmutableArray.Create(new CatalogColumn("id", "INTEGER"), new CatalogColumn("email", "TEXT"), new CatalogColumn("amount", "INTEGER"), new CatalogColumn("tenant_id", "TEXT")),
            "consents" => ImmutableArray.Create(new CatalogColumn("id", "INTEGER"), new CatalogColumn("order_id", "INTEGER"), new CatalogColumn("purpose", "TEXT"), new CatalogColumn("tenant_id", "TEXT")),
            "items" => ImmutableArray.Create(new CatalogColumn("id", "INTEGER"), new CatalogColumn("order_id", "INTEGER"), new CatalogColumn("label", "TEXT"), new CatalogColumn("tenant_id", "TEXT")),
            _ => ImmutableArray.Create(new CatalogColumn("id", "INTEGER"), new CatalogColumn("item_id", "INTEGER"), new CatalogColumn("tenant_id", "TEXT"))
        },
        "tenant_id",
        1);

    public static TableMetadata DependentMetadata(TableIdentifier id, bool withTenant = true, bool active = true) => new()
    {
        Table = new Table { SourceName = DataSource, SchemaName = id.Schema, TableName = id.TableName, IsActive = active, SourceType = "Sqlite" },
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
            Table = new Table { SourceName = DataSource, SchemaName = "main", TableName = id.TableName, SourceType = "Sqlite" },
            PrimaryKeyColumns = ["id"],
            Columns = [.. columns.Select(c => new TableColumn { ColumnName = c, DataType = c is "id" or "amount" or "order_id" ? "integer" : "text" })],
            ColumnMaskingRules = maskEmail && id.TableName == "orders"
                ? new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" } }
                : new Dictionary<string, MaskingRule>()
        };
    }

    // ---- policies -------------------------------------------------------------------------------------------------------

    private static SqlIdentifier Id(string name) => new(name, true);

    private static ColumnReference Col(string alias, string column) => new(new SqlQualifiedName([Id(alias), Id(column)]));

    private static NamedTableSource Source(string table, string alias) => new(new SqlQualifiedName([Id("main"), Id(table)]), Id(alias));

    private static SelectStatement Select(string table, string alias, string column, Expression where) => new(
        null,
        new QuerySpecification(false, [new ColumnSelectItem(Col(alias, column), null)], Source(table, alias), where, null, null),
        null,
        null);

    /// <summary>EXISTS (SELECT c.order_id FROM main.consents c WHERE c.order_id = autheris_target.id AND c.purpose = :purpose).</summary>
    public static IRowPolicy ExistsPolicy(string purpose = "analytics")
    {
        var namer = new PolicyParameterNamer("__rf_");
        var parameter = namer.Add(new PolicyValue(purpose, SqlParameterType.String));
        var where = new BinaryExpression(
            new BinaryExpression(Col("c", "order_id"), BinaryOperator.Equal, PolicyParameterNamer.Column($"{RowFilterAliases.Target}.id")),
            BinaryOperator.And,
            new BinaryExpression(Col("c", "purpose"), BinaryOperator.Equal, parameter));
        return new PolicyPredicateRowPolicy(
            PolicyPredicate.Create(new ExistsExpression(Select("consents", "c", "order_id", where)), namer.Values), TargetSqlDialect.Sqlite, Renderer);
    }

    /// <summary>autheris_target.id IN (SELECT c.order_id FROM main.consents c WHERE c.purpose = :purpose).</summary>
    public static IRowPolicy InPolicy(string purpose = "analytics")
    {
        var namer = new PolicyParameterNamer("__rf_");
        var parameter = namer.Add(new PolicyValue(purpose, SqlParameterType.String));
        var where = new BinaryExpression(Col("c", "purpose"), BinaryOperator.Equal, parameter);
        return new PolicyPredicateRowPolicy(
            PolicyPredicate.Create(
                new InSubqueryExpression(PolicyParameterNamer.Column($"{RowFilterAliases.Target}.id"), Select("consents", "c", "order_id", where), false),
                namer.Values),
            TargetSqlDialect.Sqlite,
            Renderer);
    }

    /// <summary>EXISTS (SELECT ic.item_id FROM main.item_consents ic WHERE ic.item_id = autheris_target.id), on the child table.</summary>
    public static IRowPolicy ItemPolicy()
    {
        var where = new BinaryExpression(Col("ic", "item_id"), BinaryOperator.Equal, PolicyParameterNamer.Column($"{RowFilterAliases.Target}.id"));
        return new PolicyPredicateRowPolicy(
            PolicyPredicate.Create(new ExistsExpression(Select("item_consents", "ic", "item_id", where)), new Dictionary<string, PolicyValue>()),
            TargetSqlDialect.Sqlite,
            Renderer);
    }

    /// <param name="maskEmail">The email column is masked (typed REDACT mask plus the Mask access level), as a production decision carries it.</param>
    /// <param name="extraAccess">Further column levels (a restricted-scope token denies columns, Issue #143).</param>
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
                    ["email"] = Autheris.Application.Sql.Masking.SqlDataMaskingProvider.BuildUnkeyedMaskSpec(null, "email", DatabaseDialect.Sqlite)
                })
            }
            : decision;
    }

    public static ResolvedTableAccess Resolved(TableMetadata metadata, TableAccessDecision decision, string tenant) =>
        new(metadata, decision, new TenantId(tenant), new Sid("user"), new ClaimsPrincipal());

    // ---- the two paths ---------------------------------------------------------------------------------------------------

    public async Task<TreeTableAccess> TreeAccessAsync(
        TableIdentifier table, IRowPolicy? policy, string tenant, bool maskEmail = false, IReadOnlyDictionary<string, ColumnAccessLevel>? extraAccess = null)
    {
        var meta = Meta(table, maskEmail);
        var decision = Decision(table, policy, maskEmail, extraAccess);
        TreeSecuredSource? source = null;
        if (TreeSqlCompiler.RowPolicyRequiresEnginePipeline(decision))
        {
            source = await Builder.BuildAsync(Resolved(meta, decision, tenant), DataSource, DatabaseDialect.Sqlite, CancellationToken.None);
        }

        return new TreeTableAccess(meta, decision, "tenant_id", tenant, source);
    }

    public string RunTree(TreeQueryNode root, IReadOnlyDictionary<TableIdentifier, TreeTableAccess> access)
    {
        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.Sqlite);
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = compiled.Sql;
        foreach (var (name, value) in compiled.Parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return (string)cmd.ExecuteScalar()!;
    }

    public async Task<List<long>> TreeOrderIdsAsync(IRowPolicy policy, string tenant)
    {
        var access = new Dictionary<TableIdentifier, TreeTableAccess> { [OrdersId] = await TreeAccessAsync(OrdersId, policy, tenant) };
        var root = new TreeQueryNode(OrdersId, ["id", "email"]) { OrderBy = [new TreeOrder("id")], Limit = 100 };
        using var doc = JsonDocument.Parse(RunTree(root, access));
        return [.. doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetInt64())];
    }

    /// <summary>The WebSQL path: the same factory and engine, user statement <c>SELECT id FROM orders</c>, bound through the SQLite binder.</summary>
    public Task<List<long>> CompileOrderIdsAsync(IRowPolicy policy, string tenant) => CompileIdsAsync(OrdersId, policy, tenant);

    public Task<List<long>> CompileItemIdsAsync(IRowPolicy policy, string tenant) => CompileIdsAsync(ItemsId, policy, tenant);

    private async Task<List<long>> CompileIdsAsync(TableIdentifier table, IRowPolicy policy, string tenant)
    {
        var meta = Meta(table);
        var identity = new TableIdentity("main", table.TableName);
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
        var request = CompileRequestFactory.Create(governed, TargetSqlDialect.Sqlite, new WebSqlOptions(), false, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var compiled = Engine.Compile($"SELECT id FROM {table.TableName} ORDER BY id".AsMemory(), request, CancellationToken.None);

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = compiled.Sql;
        CompiledSqlBinders.For(TargetSqlDialect.Sqlite).Bind(cmd, compiled, new Dictionary<string, object?>());
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }
}
