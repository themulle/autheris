using System.Collections.Frozen;
using System.Collections.Immutable;
using DuckDB.NET.Data;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Issue #1038: Filter-first evaluation fence tests per dialect and strict refusals where fencing does not apply.
/// </summary>
[Trait("Category", "SqlGate")]
public sealed class EvaluationFenceTests
{
    private readonly FastSqlEngine _engine = new();

    private static PolicyPredicate RegionOrPolicy() => PolicyPredicate.Create(
        new BinaryExpression(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
                BinaryOperator.Equal,
                new PolicyParameterExpression("__pol_r1", SqlParameterType.String)),
            BinaryOperator.Or,
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
                BinaryOperator.Equal,
                new PolicyParameterExpression("__pol_r2", SqlParameterType.String))),
        new Dictionary<string, PolicyValue>
        {
            ["__pol_r1"] = new("Nord", SqlParameterType.String),
            ["__pol_r2"] = new("Ost", SqlParameterType.String)
        });

    private static CompileRequest Request(TargetSqlDialect dialect, StatementPermissions permissions = StatementPermissions.ReadOnly)
    {
        var rowFilters = new DictPolicyProvider();
        rowFilters.Predicates[Orders] = RegionOrPolicy();
        rowFilters.NoPolicy.Add(Entitlements);
        rowFilters.NoPolicy.Add(Lookup);

        return new CompileRequest
        {
            TargetDialect = dialect,
            Statements = permissions,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            AllowedFunctions = SqlFunctionAllowlists.Build(dialect, null),
            AllowExperimentalDialect = true,
            Policy = new GovernancePolicy
            {
                RowFilters = rowFilters,
                Masks = new DictMaskProvider(),
                Catalog = Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "OFFSET 0")]
    [InlineData(TargetSqlDialect.SqlServer, "ORDER BY (SELECT NULL) OFFSET 0 ROWS")]
    [InlineData(TargetSqlDialect.Oracle, "AND ROWNUM >= 1")]
    public void FencedDialects_EmitExpectedEvaluationBarrier_OnRowPolicyTableOnly(TargetSqlDialect dialect, string expectedBarrier)
    {
        const string sql = """
            SELECT SUM(o.Amount * 2) AS doubled
            FROM Orders o
            JOIN Lookup l ON o.Status = l.Code
            """;

        var compiled = _engine.Compile(sql.AsMemory(), Request(dialect), CancellationToken.None);

        Assert.Contains(expectedBarrier, compiled.Sql, StringComparison.Ordinal);

        // Oracle OR predicate inside policy must remain parenthesized before AND ROWNUM >= 1.
        if (dialect == TargetSqlDialect.Oracle)
        {
            Assert.Contains("WHERE ((", compiled.Sql, StringComparison.Ordinal);
            Assert.Contains(") AND ROWNUM >= 1)", compiled.Sql, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    public void FencedDialects_AllowCallerExpressions_Arithmetic_Cast_Functions_Case_Aggregates(TargetSqlDialect dialect)
    {
        string[] queries =
        [
            "SELECT SUM(Amount * 2), AVG(Amount / 100), Id + 1 FROM Orders GROUP BY Id",
            "SELECT CAST(Status AS integer), ROUND(Amount, 1), ABS(Id) FROM Orders",
            "SELECT CASE WHEN Amount > 0 THEN Amount / 2 ELSE 0 END FROM Orders WHERE sqrt(Amount) > 1",
            "WITH src AS (SELECT Id, Amount FROM Orders) SELECT Id, SUM(Amount * 3) OVER (PARTITION BY Id) FROM src",
            "SELECT o.Id, o.Amount / 10 FROM Orders o JOIN Entitlements e ON o.Id = e.OrderId WHERE o.Amount > 5"
        ];

        foreach (var sql in queries)
        {
            var compiled = _engine.Compile(sql.AsMemory(), Request(dialect), CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(compiled.Sql));
        }
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(TargetSqlDialect.Databricks)]
    [InlineData(TargetSqlDialect.DuckDb)]
    public void UnfencedDialects_RefuseRaisingCallerExpressions_WithCodeAndSafeHint(TargetSqlDialect dialect)
    {
        var ex = Assert.Throws<RowPolicyExpressionRejectedException>(() =>
            _engine.Compile("SELECT Amount * 2 FROM Orders".AsMemory(), Request(dialect), CancellationToken.None));

        Assert.Equal(GovernedSqlErrorCodes.RowPolicyExpressionNotAllowed, ex.Code);
        Assert.Equal(RowPolicyErrorGuard.Message, ex.Message);
        Assert.Contains("TRY_CAST", ex.Hint, StringComparison.Ordinal);
        Assert.Contains("NULLIF", ex.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("Amount", ex.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("Orders", ex.Hint, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void AnsiDialect_IsRejectedFailClosed()
    {
        Assert.Throws<SqlCompileNotSupportedException>(() =>
            _engine.Compile("SELECT Amount * 2 FROM Orders".AsMemory(), Request(TargetSqlDialect.Ansi), CancellationToken.None));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    public void FencedDialects_StillRefuseDml_Coercions_AndConstantHazards(TargetSqlDialect dialect)
    {
        var dmlRequest = Request(dialect, StatementPermissions.Delete).WithTestWritePermit("DELETE FROM Orders WHERE Amount / Id > 1");
        Assert.Throws<RowPolicyExpressionRejectedException>(() =>
            _engine.Compile("DELETE FROM Orders WHERE Amount / Id > 1".AsMemory(), dmlRequest, CancellationToken.None));

        string[] alwaysRefusedReads =
        [
            "SELECT Id FROM Orders WHERE Id = '5'",
            "SELECT Id FROM Orders WHERE CASE WHEN Id = 42 THEN sqrt(-1) ELSE 0 END = 0",
            "SELECT Id FROM Orders WHERE CASE WHEN Id = 42 THEN CAST('abc' AS integer) ELSE 0 END = 0",
            "SELECT Id FROM Orders WHERE 9223372036854775807 + 1 > 0"
        ];

        foreach (var sql in alwaysRefusedReads)
        {
            var ex = Assert.Throws<RowPolicyExpressionRejectedException>(() =>
                _engine.Compile(sql.AsMemory(), Request(dialect), CancellationToken.None));
            Assert.Equal(GovernedSqlErrorCodes.RowPolicyExpressionNotAllowed, ex.Code);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FencedDialect_ClientBindFamilyMismatch_ThrowsRowPolicyExpressionRejectedException()
    {
        var compiled = _engine.Compile(
            "SELECT Id FROM Orders WHERE Id = __param_id".AsMemory(),
            Request(TargetSqlDialect.PostgreSql),
            CancellationToken.None);

        using var cmd = new DuckDBCommand();
        var ex = Assert.Throws<RowPolicyExpressionRejectedException>(() =>
            CompiledSqlBinders.For(TargetSqlDialect.PostgreSql).Bind(
                cmd,
                compiled,
                new Dictionary<string, object?> { ["id"] = "not-an-integer" }));

        Assert.Equal(GovernedSqlErrorCodes.RowPolicyExpressionNotAllowed, ex.Code);
        Assert.Contains("TRY_CAST", ex.Hint, StringComparison.Ordinal);
        Assert.Contains("NULLIF", ex.Hint, StringComparison.Ordinal);
    }

    private static CompileRequest OraclePerCellRequest(bool protectEntitlements = false)
    {
        var existsSubquery = new SelectStatement(null,
            new QuerySpecification(
                false,
                [new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null)],
                new NamedTableSource(
                    new SqlQualifiedName([new SqlIdentifier("dbo", true), new SqlIdentifier("Lookup", true)]),
                    new SqlIdentifier("l", true)),
                new BinaryExpression(
                    new BinaryExpression(
                        new ColumnReference(new SqlQualifiedName([new SqlIdentifier("l", true), new SqlIdentifier("Code", true)])),
                        BinaryOperator.Equal,
                        new ColumnReference(new SqlQualifiedName([new SqlIdentifier(RowFilterAliases.Target), new SqlIdentifier("Status", true)]))),
                    BinaryOperator.Or,
                    new BinaryExpression(
                        new ColumnReference(new SqlQualifiedName([new SqlIdentifier("l", true), new SqlIdentifier("Code", true)])),
                        BinaryOperator.Equal,
                        new PolicyParameterExpression("__pol_code", SqlParameterType.String))),
                null, null),
            null, null);

        var existsPredicate = PolicyPredicate.Create(
            new ExistsExpression(existsSubquery),
            new Dictionary<string, PolicyValue> { ["__pol_code"] = new("VIP", SqlParameterType.String) });

        var regionPredicate = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName([new SqlIdentifier("Region", true)])),
                BinaryOperator.Equal,
                new PolicyParameterExpression("__pol_r1", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_r1"] = new("Nord", SqlParameterType.String) });

        var perCell = new PerCellTablePolicy(
            [new PerCellSignatureSpec(existsPredicate), new PerCellSignatureSpec(regionPredicate)],
            new Dictionary<string, PerCellColumnRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["Email"] = new(ImmutableArray.Create(0), ImmutableArray.Create(1))
            });

        var rowFilters = new DictPolicyProvider();
        rowFilters.NoPolicy.Add(Orders);
        rowFilters.NoPolicy.Add(Lookup);
        if (protectEntitlements)
        {
            rowFilters.Predicates[Entitlements] = PolicyPredicate.Create(
                new BinaryExpression(
                    new ColumnReference(new SqlQualifiedName([new SqlIdentifier("OrderId", true)])),
                    BinaryOperator.GreaterThan,
                    new PolicyParameterExpression("__pol_min_order", SqlParameterType.Int32)),
                new Dictionary<string, PolicyValue> { ["__pol_min_order"] = new(0, SqlParameterType.Int32) });
        }
        else
        {
            rowFilters.NoPolicy.Add(Entitlements);
        }

        var masks = new DictMaskProvider();
        masks.Masks[(Orders, "email")] = new MaskSpec(
            MaskKind.Redact,
            new MaskArguments(Constant: new PolicyParameterExpression("__mask_c", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__mask_c"] = new("***", SqlParameterType.String) }.ToFrozenDictionary());

        var oracleCatalog = new InMemoryTableCatalog(
        [
            new TableCatalogEntry(Orders, ImmutableArray.Create(
                new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "varchar2(64)"), new CatalogColumn("Region", "varchar2(20)"),
                new CatalogColumn("Status", "varchar2(20)"), new CatalogColumn("Amount", "number(18,2)"), new CatalogColumn("Email", "varchar2(200)")),
                "TenantId", 1),
            new TableCatalogEntry(Entitlements, ImmutableArray.Create(
                new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "varchar2(64)"), new CatalogColumn("OrderId", "int")),
                "TenantId", 1),
            new TableCatalogEntry(Lookup, ImmutableArray.Create(new CatalogColumn("Code", "varchar2(10)")), null, 1)
        ], "dbo");

        return new CompileRequest
        {
            TargetDialect = TargetSqlDialect.Oracle,
            Statements = StatementPermissions.ReadOnly,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            AllowedFunctions = SqlFunctionAllowlists.Build(TargetSqlDialect.Oracle, null),
            AllowExperimentalDialect = true,
            Policy = new GovernancePolicy
            {
                RowFilters = rowFilters,
                Masks = masks,
                PerCellColumns = new StaticPerCellProvider(Orders, perCell),
                Catalog = oracleCatalog,
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Oracle_PerCellWithUndecomposedExistsInProjection_KeepsRowNumFenceOnOuterSubquery()
    {
        // Raising arithmetic (Amount / 10) succeeds only when evaluationFenced == true.
        var compiled = _engine.Compile("SELECT Amount / 10, Email FROM Orders".AsMemory(), OraclePerCellRequest(), CancellationToken.None);

        Assert.Contains("EXISTS (", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(") AND ROWNUM >= 1)", compiled.Sql, StringComparison.Ordinal);

        int ordersFromIndex = compiled.Sql.IndexOf("\"dbo\".\"Orders\"", StringComparison.Ordinal);
        Assert.True(ordersFromIndex > 0, compiled.Sql);

        // The undecomposed EXISTS inside the PerCell CASE projection must not steal ROWNUM >= 1 from the outer subquery.
        Assert.DoesNotContain("ROWNUM", compiled.Sql[..ordersFromIndex], StringComparison.Ordinal);
        int rowNumIndex = compiled.Sql.IndexOf("AND ROWNUM >= 1", StringComparison.Ordinal);
        Assert.True(rowNumIndex > ordersFromIndex, compiled.Sql);
        Assert.Equal(-1, compiled.Sql.IndexOf("ROWNUM", rowNumIndex + "AND ROWNUM >= 1".Length, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Oracle_PerCellWithFencedInnerExistsTable_KeepsRowNumFenceOnBothSubqueries()
    {
        const string sql = "SELECT CASE WHEN EXISTS (SELECT 1 FROM Entitlements e WHERE e.OrderId = o.Id) THEN o.Amount / 10 ELSE 0 END, o.Email FROM Orders o";
        var compiled = _engine.Compile(sql.AsMemory(), OraclePerCellRequest(protectEntitlements: true), CancellationToken.None);

        int entitlementsIdx = compiled.Sql.IndexOf("\"dbo\".\"Entitlements\"", StringComparison.Ordinal);
        int ordersIdx = compiled.Sql.IndexOf("\"dbo\".\"Orders\"", StringComparison.Ordinal);
        Assert.True(entitlementsIdx > 0 && ordersIdx > entitlementsIdx, compiled.Sql);

        // Inner fenced Entitlements subquery gets its own ROWNUM >= 1, and outer PerCell Orders subquery still keeps its ROWNUM >= 1.
        int firstFence = compiled.Sql.IndexOf("AND ROWNUM >= 1", StringComparison.Ordinal);
        int secondFence = compiled.Sql.IndexOf("AND ROWNUM >= 1", firstFence + 1, StringComparison.Ordinal);
        Assert.True(firstFence > entitlementsIdx && firstFence < ordersIdx, compiled.Sql);
        Assert.True(secondFence > ordersIdx, compiled.Sql);
        Assert.Equal(-1, compiled.Sql.IndexOf("AND ROWNUM >= 1", secondFence + 1, StringComparison.Ordinal));
    }

    // Helper types placed after all [Fact]/[Theory] methods so SecurityTestInventoryTests records outer class FQN cleanly.
    private sealed class StaticPerCellProvider(TableIdentity target, PerCellTablePolicy policy) : IPerCellColumnProvider
    {
        public PerCellTablePolicy? GetPolicy(TableIdentity table) => table == target ? policy : null;
    }
}
