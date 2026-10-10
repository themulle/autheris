using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

public class OracleCapabilityTests
{
    private static readonly DialectCapabilities Caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.Oracle);

    [Fact]
    public void Oracle_HasTheDocumentedValues()
    {
        Assert.Equal(DialectSupportTier.Production, Caps.Tier);
        Assert.Equal(32767, Caps.MaxBindParameters);              // follows jOOQ; the Oracle Free probe test confirms it
        Assert.Equal(1000, Caps.MaxInListItems);                  // ORA-01795, tracked separately from the bind limit
        Assert.Equal(128, Caps.MaxIdentifierLength);
        Assert.Equal(IdentifierLengthUnit.Bytes, Caps.IdentifierLengthUnit);
        Assert.Equal('"', Caps.IdentifierOpenQuote);
        Assert.Equal(ParameterMarkerStyle.ColonNamedOrdinal, Caps.MarkerStyle);
        Assert.True(Caps.SupportsMarkerReuse);
        Assert.Equal(PaginationStyle.OffsetFetch, Caps.Pagination);
        Assert.True(Caps.SupportsWithTies);
        Assert.False(Caps.SupportsTryCast);
        Assert.False(Caps.SupportsFilterClause);
        Assert.False(Caps.InDbHmac);                              // true only after a DBMS_CRYPTO grant probe (WP-F6)
        Assert.Equal(TenantComparisonStyle.RawCast, Caps.TenantComparison);
        Assert.Empty(Caps.AllowedTableFunctions);
    }

    [Fact]
    public void OracleInList1001_ThrowsSqlLimitExceeded_AndBindLimitIsSeparate()
    {
        var items = Enumerable.Range(1, 1001).Select(i => (Expression)new LiteralExpression((long)i, LiteralType.Integer)).ToList();
        var stmt = new SelectStatement(null, new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
            new NamedTableSource(new SqlQualifiedName("t"), null),
            new InListExpression(new ColumnReference(new SqlQualifiedName("a")), items, false), null, null), null, null);
        var ex = Assert.Throws<SqlLimitExceededException>(() => DialectCapabilityValidator.Validate(stmt, Caps));
        Assert.Equal(SqlLimitKind.InListItems, ex.Kind);
        Assert.Equal(1000, ex.Maximum);
        // 1000 items pass the validator and are well below the bind limit
        DialectCapabilityValidator.Validate(stmt with
        {
            Body = ((QuerySpecification)stmt.Body) with { Where = new InListExpression(new ColumnReference(new SqlQualifiedName("a")), items.Take(1000).ToList(), false) }
        }, Caps);
    }
}

public class OracleCheckerTests
{
    private static BoundParameter P(int n) => new($":p{n}", $"p{n}", n - 1, "v", SqlParameterType.String, ParameterOrigin.QueryLiteral);

    private static void Check(string sql, ImmutableArray<BoundParameter>? ps = null) =>
        EmittedSqlInvariantChecker.Check(sql, TargetSqlDialect.Oracle, ps ?? ImmutableArray<BoundParameter>.Empty, Array.Empty<EmittedRange>());

    [Theory]
    [InlineData("SELECT 'a' FROM \"T\"")]
    [InlineData("SELECT q'[a]' FROM \"T\"")]
    [InlineData("SELECT nq'[a]' FROM \"T\"")]
    [InlineData("SELECT \"A\" FROM \"T\" -- x")]
    [InlineData("SELECT \"A\" FROM \"T\";")]
    [InlineData("SELECT \"A\" FROM \"T\" WHERE 1 = 1")]
    public void Rejects_QuotesAlternativeQuotingCommentsAndNumbers(string sql) =>
        Assert.ThrowsAny<EmittedSqlInvariantViolationException>(() => Check(sql));

    [Fact]
    public void ColonNamedMarkers_MapOneToOne()
    {
        Check("SELECT \"A\" FROM \"T\" WHERE \"A\" = :p1 AND \"B\" = :p2 AND \"C\" = :p1", new[] { P(1), P(2) }.ToImmutableArray());
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check("SELECT :p1 FROM \"DUAL\""));
    }
}

public class OracleGeneratorTests
{
    private static CompiledSql Gen(string sql, IDictionary<string, PolicyValue>? values = null) =>
        CompilerTestHelpers.Generate(TargetSqlDialect.Oracle, sql, values);

    [Fact]
    public void Literals_AreBound_WithNamedColonMarkers_AndUpperFoldedUnquotedIdentifiers()
    {
        var c = Gen("SELECT id FROM t WHERE a = 'x' AND b > 5 AND c = 'x'");
        Assert.DoesNotContain('\'', c.Sql);
        Assert.Contains("\"A\" = :p1", c.Sql);
        Assert.Contains("\"B\" > :p2", c.Sql);
        Assert.Equal(2, c.Parameters.Length);
        Assert.Equal("p1", c.Parameters[0].Name);
    }

    [Fact]
    public void FromlessSelect_UsesDual() => Assert.Contains("FROM DUAL", Gen("SELECT 1 AS x").Sql);

    [Fact]
    public void Pagination_IsStructural()
    {
        var c = Gen("SELECT id FROM t ORDER BY id OFFSET 5 LIMIT 10");
        Assert.Contains("OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY", c.Sql);
        Assert.Empty(c.Parameters);
        Assert.Contains("FETCH FIRST 3 ROWS WITH TIES", Gen("SELECT id FROM t ORDER BY id FETCH FIRST 3 ROWS WITH TIES").Sql);
    }

    [Fact]
    public void Strpos_MapsToInstr() => Assert.Contains("INSTR(\"A\", :p1)", Gen("SELECT strpos(a, 'x') FROM t").Sql);

    [Fact]
    public void TryCast_IsRejected() => Assert.ThrowsAny<Exception>(() => Gen("SELECT TRY_CAST(a AS integer) FROM t"));

    [Theory]
    [InlineData("SELECT a, SUM(b) OVER (PARTITION BY a ORDER BY c ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS s FROM t")]
    [InlineData("SELECT CAST(a AS decimal(10, 2)) AS d, CAST(b AS double) AS i, CAST(c AS varchar) AS v FROM t")]
    [InlineData("SELECT EXTRACT(year FROM d) AS y, EXTRACT(week FROM d) AS w FROM t")]
    [InlineData("SELECT a FROM t ORDER BY a DESC NULLS LAST, b ASC NULLS FIRST")]
    [InlineData("SELECT a, GROUPING(a) AS g FROM t GROUP BY ROLLUP (a)")]
    [InlineData("SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS v (x, y)")]
    [InlineData("SELECT DATE '2024-01-15' AS d, TIMESTAMP '2024-01-15 10:30:00' AS ts FROM t")]
    [InlineData("SELECT a IS DISTINCT FROM b AS d FROM t")]
    [InlineData("SELECT current_date, current_timestamp FROM t")]
    [InlineData("SELECT t.a FROM t JOIN u ON t.id = u.id WHERE EXISTS (SELECT 1 FROM v WHERE v.id = t.id)")]
    [InlineData("WITH c AS (SELECT a FROM t) SELECT a FROM c UNION ALL SELECT a FROM c")]
    [InlineData("SELECT a FROM t WHERE a IN (1, 2, 3) AND b BETWEEN 1 AND 5 AND c LIKE 'a%'")]
    [InlineData("SELECT true AS a, false AS b, NULL AS c FROM t")]
    [InlineData("SELECT a > 1 AS f, CASE WHEN a > 1 THEN 'x' ELSE 'y' END AS c FROM t WHERE true")]
    [InlineData("SELECT substring(a FROM 2 FOR 3) AS s, trim(BOTH 'x' FROM a) AS tr, position('a' IN b) AS p FROM t")]
    [InlineData("SELECT o.id FROM o CROSS JOIN LATERAL (SELECT 1 AS x FROM p WHERE p.id = o.id) l")]
    public void OracleEmissionPaths_PassCheckerInBoundMode(string sql)
    {
        var c = Gen(sql);
        Assert.DoesNotContain(';', c.Sql);
    }

    [Fact]
    public void TranslatedDateFunctions_AreBound_WithReviewedUnitFragments()
    {
        var stmt = new TrinoSqlEngine.Ast.Builder.SqlAstBuilder(new TrinoSqlEngine.Ast.Builder.AstBuilderOptions { TranslateTrinoDateFunctions = true })
            .BuildStatement(new FastSqlEngine().Parse("SELECT date_add('day', 3, d) AS a, date_add('month', 2, d) AS m, date_trunc('month', d) AS t FROM t".AsMemory(), SqlTokenSecurityOptions.None).Tree);
        var c = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.Oracle).Generate(stmt, ParameterSource.Empty, CancellationToken.None);
        Assert.Contains("NUMTODSINTERVAL(:p1, 'DAY')", c.Sql);
        Assert.Contains("ADD_MONTHS(\"D\", :p2)", c.Sql);
        Assert.Contains("TRUNC(\"D\", 'MM')", c.Sql);
    }

    // ---- tenant predicate ----

    [Fact]
    public void TenantPredicate_ComparesRawBytes()
    {
        var expr = TrinoSqlEngine.Ast.Security.TenantPredicateFactory.Build(
            DialectCapabilityTable.Default.Get(TargetSqlDialect.Oracle), "TENANT_ID", "__t", SqlParameterType.String);
        var stmt = new SelectStatement(null, new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
            new NamedTableSource(new SqlQualifiedName("t"), null),
            new SecurityPredicateExpression(expr, new SecurityPredicateId("APP.T", 0), SecurityScope.Root), null, null), null, null);
        var c = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.Oracle)
            .Generate(stmt, new ParameterSource(new Dictionary<string, PolicyValue> { ["__t"] = new("acme", SqlParameterType.String) }.ToFrozenDictionary(), new Dictionary<string, object?>()), CancellationToken.None);

        Assert.Contains("\"TENANT_ID\" = :p1", c.Sql);
        Assert.Contains("\"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(\"TENANT_ID\" AS VARCHAR2(4000))) = \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(:p1 AS VARCHAR2(4000)))", c.Sql);   // CR-ADG-24: schema-qualified, never resolved through the current schema
        Assert.Single(c.Parameters);
    }

    // ---- masks ----

    private static ColumnReference Col(string n) => new(new SqlQualifiedName(new[] { new SqlIdentifier(n, true) }));
    private static PolicyParameterExpression P(string n, SqlParameterType t) => new(n, t, ParameterOrigin.Mask);

    private static CompiledSql Mask(MaskExpression m, Dictionary<string, PolicyValue>? values = null)
    {
        var stmt = new SelectStatement(null, new QuerySpecification(false,
            new SelectItem[] { new ColumnSelectItem(m, new SqlIdentifier(m.Column.Name.SimpleName, true)) },
            new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("APP", true), new SqlIdentifier("T", true) }), null), null, null, null), null, null);
        return CompilerTestHelpers.Generate(TargetSqlDialect.Oracle, stmt, values);
    }

    [Fact]
    public void Nullify_IsTypedNull() =>
        Assert.Contains("CAST(NULL AS VARCHAR2(200)) AS \"EMAIL\"", Mask(new MaskExpression(MaskKind.Nullify, Col("EMAIL"), new MaskArguments(), "VARCHAR2(200)")).Sql);

    [Theory]
    [InlineData("NUMBER(18,2)")]
    [InlineData("DATE")]
    [InlineData("TIMESTAMP")]
    [InlineData("BINARY_DOUBLE")]
    public void Redact_NumericOrTemporal_IsTypedNull(string type) =>
        Assert.Contains($"CAST(NULL AS {type}) AS \"X\"", Mask(new MaskExpression(MaskKind.Redact, Col("X"), new MaskArguments(), type)).Sql);

    [Fact]
    public void Redact_Text_BindsTheReplacement()
    {
        var c = Mask(new MaskExpression(MaskKind.Redact, Col("EMAIL"), new MaskArguments(Constant: P("m", SqlParameterType.String)), "VARCHAR2(200)"),
            new() { ["m"] = new("[REDACTED]", SqlParameterType.String) });
        Assert.DoesNotContain('\'', c.Sql);
        Assert.Equal("[REDACTED]", Assert.Single(c.Parameters).Value);
    }

    [Fact]
    public void PartialMask_BindsAllArguments_AndClampsCounts()
    {
        var c = Mask(new MaskExpression(MaskKind.PartialMask, Col("EMAIL"),
                new MaskArguments(KeepPrefix: P("p", SqlParameterType.Int32), KeepSuffix: P("s", SqlParameterType.Int32), MaskChar: P("c", SqlParameterType.String)), "VARCHAR2(200)"),
            new()
            {
                ["p"] = new(2, SqlParameterType.Int32), ["s"] = new(4, SqlParameterType.Int32), ["c"] = new("*", SqlParameterType.String)
            });
        Assert.DoesNotContain('\'', c.Sql);
        Assert.Contains("GREATEST(CAST(:p1 AS NUMBER(10)), 0)", c.Sql);
        Assert.Equal(3, c.Parameters.Length);
    }

    [Fact]
    public void Hmac_IsNotAvailable_FailsClosed() =>
        Assert.ThrowsAny<Exception>(() => Mask(new MaskExpression(MaskKind.Hmac, Col("EMAIL"),
            new MaskArguments(HmacKey: P("k", SqlParameterType.Binary), HmacKeyOuter: P("o", SqlParameterType.Binary)), "VARCHAR2(200)"),
            new() { ["k"] = new(new byte[] { 1 }, SqlParameterType.Binary), ["o"] = new(new byte[] { 2 }, SqlParameterType.Binary) }));

    // ---- binder (SEC-ADG-03, SEC-ADG-17) ----

    private sealed class BindByNameCommand : SqliteCommand
    {
        public bool BindByName { get; set; }
    }

    private static CompiledSql Compiled(params BoundParameter[] ps) =>
        new("SELECT :p1 FROM DUAL", ps.ToImmutableArray(), TargetSqlDialect.Oracle, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "v");

    [Fact]
    public void OracleBinder_SetsBindByName_AndUsesNamedParameters()
    {
        var binder = new OracleCompiledSqlBinder();
        Assert.True(binder.CanBind(TargetSqlDialect.Oracle));
        using var cmd = new BindByNameCommand();
        binder.Bind(cmd, Compiled(new BoundParameter(":p1", "p1", 0, "acme", SqlParameterType.String, ParameterOrigin.Tenant, "t")), new Dictionary<string, object?>());
        Assert.True(cmd.BindByName);
        Assert.Equal("p1", cmd.Parameters[0].ParameterName);
    }

    [Fact]
    public void OracleBinder_CommandWithoutBindByName_FailsClosed()
    {
        var binder = new OracleCompiledSqlBinder();
        using var cmd = new SqliteCommand();
        Assert.ThrowsAny<System.Security.SecurityException>(() => binder.Bind(cmd, Compiled(), new Dictionary<string, object?>()));
    }

    [Theory]
    [InlineData(ParameterOrigin.Tenant)]
    [InlineData(ParameterOrigin.Policy)]
    public void OracleBinder_EmptyTenantOrPolicyString_Throws(ParameterOrigin origin)
    {
        var binder = new OracleCompiledSqlBinder();
        using var cmd = new BindByNameCommand();
        Assert.ThrowsAny<System.Security.SecurityException>(() =>
            binder.Bind(cmd, Compiled(new BoundParameter(":p1", "p1", 0, "", SqlParameterType.String, origin, "t")), new Dictionary<string, object?>()));
    }

    [Fact]
    public void OracleBinder_OutOfOrderMarkers_BindByName_ClientNamesCannotAlias()
    {
        var binder = new OracleCompiledSqlBinder();
        using var cmd = new BindByNameCommand();
        var compiled = new CompiledSql("SELECT :p2, :p1 FROM DUAL", ImmutableArray.Create(
                new BoundParameter(":p1", "p1", 0, "acme", SqlParameterType.String, ParameterOrigin.Tenant, "t"),
                new BoundParameter(":p2", "p2", 1, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "foo")),
            TargetSqlDialect.Oracle, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "v");
        binder.Bind(cmd, compiled, new Dictionary<string, object?> { ["foo"] = "bar", ["p1"] = "evil", [":p1"] = "evil" });
        Assert.Equal("acme", cmd.Parameters["p1"].Value);
        Assert.Equal("bar", cmd.Parameters["p2"].Value);
    }
}
