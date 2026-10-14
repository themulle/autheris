using System.Collections.Frozen;
using System.Text.Json;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Sql;
using Microsoft.Data.Sqlite;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// WP-ARC-B-03 / decision G-6: <c>TreeSqlCompiler</c> is the single documented exception to <c>ISqlEngine.Compile</c>. This test is the
/// guard against drift: for the same table, tenant, row filter and mask the tree compiler and the governed compile path (bound through the
/// SQLite binder) must expose the same visible rows and the same masked values.
/// </summary>
public sealed class TreeVsCompilePolicyParityTests : IDisposable
{
    private static readonly TableIdentifier Orders = new("shop", "main", "orders");

    private readonly SqliteConnection _connection;

    public TreeVsCompilePolicyParityTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE orders (id INTEGER PRIMARY KEY, email TEXT, amount INTEGER, tenant_id TEXT);
            INSERT INTO orders VALUES (1, 'a@x.example', 5, 't1'), (2, 'b@x.example', 10, 't1'), (3, 'c@x.example', 20, 't1'),
                                      (4, 'd@x.example', 30, 't2'), (5, 'e@x.example', 40, 't2');
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private static TableMetadata OrdersMeta(bool masked) => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "shop", SchemaName = "main", TableName = "orders", SourceType = "Sqlite" },
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
        new(new SqlQualifiedName(new List<SqlIdentifier> { new(name, false) }));

    private static PolicyPredicate AmountAtLeast(long minimum) => PolicyPredicate.Create(
        new BinaryExpression(Col("amount"), BinaryOperator.GreaterThanOrEqual, new PolicyParameterExpression("__pol_amount", SqlParameterType.Int64)),
        new Dictionary<string, PolicyValue> { ["__pol_amount"] = new(minimum, SqlParameterType.Int64) });

    private static MaskSpec RedactSpec() => new(
        MaskKind.Redact,
        new MaskArguments(Constant: new PolicyParameterExpression("__mask_email", SqlParameterType.String, ParameterOrigin.Mask)),
        new Dictionary<string, PolicyValue> { ["__mask_email"] = new("***", SqlParameterType.String) }.ToFrozenDictionary());

    private List<(long Id, string? Email)> ViaTree(string tenant, string? rowFilter, bool masked)
    {
        var decision = TableAccessDecision.Allowed(
            Orders,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["tenant_id"] = ColumnAccessLevel.Deny },
            rowFilter, hasUnconstrainedColumnAllow: true, null);
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Orders] = new(OrdersMeta(masked), decision, "tenant_id", tenant)
        };
        var root = new TreeQueryNode(Orders, ["id", "email"]) { OrderBy = [new TreeOrder("id")], Limit = 100 };
        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.Sqlite);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = compiled.Sql;
        foreach (var (name, value) in compiled.Parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var json = (string)cmd.ExecuteScalar()!;
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => (e.GetProperty("id").GetInt64(), e.GetProperty("email").GetString()))];
    }

    private List<(long Id, string? Email)> ViaCompile(string tenant, PolicyPredicate? policy, bool masked)
    {
        var env = new TestCompileEnv(TargetSqlDialect.Sqlite)
            .Table("orders", "tenant_id", "id:INTEGER", "email", "amount:INTEGER", "tenant_id");
        if (policy != null)
        {
            env.Policy("orders", policy);
        }

        if (masked)
        {
            env.Mask("orders", "email", RedactSpec());
        }

        var compiled = env.Compile("SELECT id, email FROM orders ORDER BY id", tenant);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = compiled.Sql;
        CompiledSqlBinders.For(TargetSqlDialect.Sqlite).Bind(cmd, compiled, new Dictionary<string, object?>());
        using var reader = cmd.ExecuteReader();
        var rows = new List<(long, string?)>();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
    }

    [Theory]
    [InlineData("t1", false)]
    [InlineData("t2", false)]
    [InlineData("t1", true)]
    [InlineData("t2", true)]
    public void TenantOnly_TreeAndCompileExposeTheSameRows(string tenant, bool masked)
    {
        var tree = ViaTree(tenant, rowFilter: null, masked);
        var compiled = ViaCompile(tenant, policy: null, masked);

        tree.ShouldNotBeEmpty();
        tree.ShouldBe(compiled);
    }

    [Theory]
    [InlineData("t1", 10, false)]
    [InlineData("t1", 10, true)]
    [InlineData("t2", 40, true)]
    public void TenantRowFilterAndMask_TreeAndCompileExposeTheSameRows(string tenant, long minimum, bool masked)
    {
        var tree = ViaTree(tenant, $"\"amount\" >= {minimum}", masked);
        var compiled = ViaCompile(tenant, AmountAtLeast(minimum), masked);

        tree.ShouldNotBeEmpty();
        tree.ShouldBe(compiled);
        if (masked)
        {
            tree.ShouldAllBe(r => r.Email == "***");
        }
    }

    [Fact]
    public void TenantIsolation_NeitherPathReturnsAForeignTenantRow()
    {
        ViaTree("t1", null, masked: false).Select(r => r.Id).ShouldBe(new long[] { 1, 2, 3 });
        ViaCompile("t1", null, masked: false).Select(r => r.Id).ShouldBe(new long[] { 1, 2, 3 });
    }

    [Fact]
    public void RowFilterThatMatchesNothing_YieldsNoRowsOnBothPaths()
    {
        ViaTree("t1", "\"amount\" >= 1000", masked: false).ShouldBeEmpty();
        ViaCompile("t1", AmountAtLeast(1000), masked: false).ShouldBeEmpty();
    }
}
