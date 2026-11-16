namespace Autheris.Tests.Integration.GraphQL;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.TestDoubles;
using Autheris.Tests.Unit.GraphQL;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-ARC-B-03 / decision G-6: PostgreSQL parity guard between <c>TreeSqlCompiler</c> and <c>FastSqlEngine.Compile</c>.
/// Both paths must expose the exact same visible rows and masked values.
/// </summary>
public sealed class TreeVsCompilePolicyParityTests
{
    private static readonly TableIdentifier Orders = new("shop", "public", "orders");

    private static async Task SeedAsync(string connectionString)
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
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static TableMetadata OrdersMeta(bool masked) => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "shop", SchemaName = "public", TableName = "orders", SourceType = "PostgreSql" },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "email", DataType = "text" },
            new TableColumn { ColumnName = "amount", DataType = "integer" },
            new TableColumn { ColumnName = "tenant_id", DataType = "text" }
        ],
        ColumnMaskingRules = masked
            ? new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" } }
            : new Dictionary<string, MaskingRule>()
    };

    private static ColumnReference Col(string name) =>
        new(new SqlQualifiedName([new SqlIdentifier(name, false)]));

    private static PolicyPredicate AmountAtLeast(long minimum) => PolicyPredicate.Create(
        new BinaryExpression(Col("amount"), BinaryOperator.GreaterThanOrEqual, new PolicyParameterExpression("__pol_amount", SqlParameterType.Int64)),
        new Dictionary<string, PolicyValue> { ["__pol_amount"] = new(minimum, SqlParameterType.Int64) });

    private static MaskSpec RedactSpec() => new(
        MaskKind.Redact,
        new MaskArguments(Constant: new PolicyParameterExpression("__mask_email", SqlParameterType.String, ParameterOrigin.Mask)),
        new Dictionary<string, PolicyValue> { ["__mask_email"] = new("***", SqlParameterType.String) }.ToFrozenDictionary());

    private static async Task<List<(long Id, string? Email)>> ViaTreeAsync(string connectionString, string tenant, IRowPolicy? rowFilter, bool masked)
    {
        var decision = TypedTreeRowPolicy.Allowed(
            Orders,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["tenant_id"] = ColumnAccessLevel.Deny },
            rowFilter);
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Orders] = new(OrdersMeta(masked), decision, "tenant_id", tenant)
        };
        var root = new TreeQueryNode(Orders, ["id", "email"]) { OrderBy = [new TreeOrder("id")], Limit = 100 };
        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.PostgreSql);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = compiled.Sql;
        foreach (var (name, value) in compiled.Parameters)
        {
            cmd.Parameters.AddWithValue(name.TrimStart('@'), value ?? DBNull.Value);
        }

        var json = (string)(await cmd.ExecuteScalarAsync())!;
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => (e.GetProperty("id").GetInt64(), e.GetProperty("email").GetString()))];
    }

    private static async Task<List<(long Id, string? Email)>> ViaCompileAsync(string connectionString, string tenant, PolicyPredicate? policy, bool masked)
    {
        var env = new ParityCompileEnv("public")
            .Table("orders", "tenant_id", "id:integer", "email", "amount:integer", "tenant_id");
        if (policy != null) env.Policy("orders", policy);
        if (masked) env.Mask("orders", "email", RedactSpec());

        var compiled = env.Compile("SELECT id, email FROM public.orders ORDER BY id", tenant);
        var binder = new PostgreSqlCompiledSqlBinder();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(long, string?)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), await reader.IsDBNullAsync(1) ? null : reader.GetString(1)));
        }

        return rows;
    }

    [RequiresContainerRuntimeTheory]
    [InlineData("t1", false)]
    [InlineData("t2", false)]
    [InlineData("t1", true)]
    [InlineData("t2", true)]
    public async Task TenantOnly_TreeAndCompileExposeTheSameRows(string tenant, bool masked)
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        var tree = await ViaTreeAsync(db.ConnectionString, tenant, rowFilter: null, masked);
        var compiled = await ViaCompileAsync(db.ConnectionString, tenant, policy: null, masked);

        tree.ShouldNotBeEmpty();
        tree.ShouldBe(compiled);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData("t1", 10, false)]
    [InlineData("t1", 10, true)]
    [InlineData("t2", 40, true)]
    public async Task TenantRowFilterAndMask_TreeAndCompileExposeTheSameRows(string tenant, long minimum, bool masked)
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        var tree = await ViaTreeAsync(db.ConnectionString, tenant, TypedTreeRowPolicy.Int("amount", BinaryOperator.GreaterThanOrEqual, minimum), masked);
        var compiled = await ViaCompileAsync(db.ConnectionString, tenant, AmountAtLeast(minimum), masked);

        tree.ShouldNotBeEmpty();
        tree.ShouldBe(compiled);
        if (masked)
        {
            tree.ShouldAllBe(r => r.Email == "***");
        }
    }

    [RequiresContainerRuntimeFact]
    public async Task TenantIsolation_NeitherPathReturnsAForeignTenantRow()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        (await ViaTreeAsync(db.ConnectionString, "t1", null, masked: false)).Select(r => r.Id).ShouldBe([1L, 2L, 3L]);
        (await ViaCompileAsync(db.ConnectionString, "t1", null, masked: false)).Select(r => r.Id).ShouldBe([1L, 2L, 3L]);
    }

    [RequiresContainerRuntimeFact]
    public async Task RowFilterThatMatchesNothing_YieldsNoRowsOnBothPaths()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        (await ViaTreeAsync(db.ConnectionString, "t1", TypedTreeRowPolicy.Int("amount", BinaryOperator.GreaterThanOrEqual, 1000), masked: false)).ShouldBeEmpty();
        (await ViaCompileAsync(db.ConnectionString, "t1", AmountAtLeast(1000), masked: false)).ShouldBeEmpty();
    }

    private sealed class ParityCompileEnv(string defaultSchema)
    {
        private readonly List<TableCatalogEntry> _tables = [];
        private readonly Policies _policies = new();
        private readonly FastSqlEngine _engine = new();

        public ParityCompileEnv Table(string name, string? tenantColumn, params string[] columns)
        {
            var cols = columns.Select(c =>
            {
                var parts = c.Split(':');
                return new CatalogColumn(parts[0], parts.Length > 1 ? parts[1] : "text");
            }).ToArray();
            _tables.Add(new TableCatalogEntry(new TableIdentity(defaultSchema, name), [.. cols], tenantColumn, 1));
            return this;
        }

        public void Policy(string table, PolicyPredicate predicate) =>
            _policies.Predicates[new TableIdentity(defaultSchema, table)] = predicate;

        public void Mask(string table, string column, MaskSpec spec) =>
            _policies.Masks[(new TableIdentity(defaultSchema, table), column.ToLowerInvariant())] = spec;

        public CompiledSql Compile(string sql, string tenant) => _engine.Compile(
            sql.AsMemory(),
            new CompileRequest
            {
                TargetDialect = TargetSqlDialect.PostgreSql,
                TokenGuards = SqlTokenSecurityOptions.Strict,
                Statements = StatementPermissions.ReadOnly,
                AllowExperimentalDialect = true,
                Policy = new GovernancePolicy
                {
                    RowFilters = _policies,
                    Masks = _policies,
                    Catalog = new InMemoryTableCatalog(_tables, defaultSchema),
                    Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String),
                    TablesWithConsentRowFilter = new HashSet<string>()
                }
            },
            System.Threading.CancellationToken.None);

        private sealed class Policies : IPolicyPredicateProvider, IColumnMaskProvider
        {
            public Dictionary<TableIdentity, PolicyPredicate> Predicates { get; } = new();
            public Dictionary<(TableIdentity, string), MaskSpec> Masks { get; } = new();
            public bool ShouldApplyPolicy(TableIdentity table) => Predicates.ContainsKey(table);
            public PolicyPredicate GetPredicate(TableIdentity table) => Predicates.TryGetValue(table, out var p) ? p : PolicyPredicate.DenyAll;
            public bool HasMask(TableIdentity table, string column) => Masks.ContainsKey((table, column.ToLowerInvariant()));
            public MaskSpec GetMask(TableIdentity table, string column) => Masks[(table, column.ToLowerInvariant())];
        }
    }
}
