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

public class DatabricksCapabilityTests
{
    private static readonly DialectCapabilities Caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.Databricks);

    [Fact]
    public void Databricks_HasTheDocumentedValues()
    {
        Assert.Equal(DialectSupportTier.Experimental, Caps.Tier);
        Assert.Equal(1000, Caps.MaxBindParameters);           // provisional fail-closed budget until the live probe (WP-C5)
        Assert.Null(Caps.MaxInListItems);
        Assert.Equal(255, Caps.MaxIdentifierLength);
        Assert.Equal(IdentifierLengthUnit.Characters, Caps.IdentifierLengthUnit);
        Assert.Equal('`', Caps.IdentifierOpenQuote);
        Assert.Equal('`', Caps.IdentifierCloseQuote);
        Assert.Equal(ParameterMarkerStyle.ColonNamedOrdinal, Caps.MarkerStyle);
        Assert.True(Caps.SupportsMarkerReuse);
        Assert.Equal(PaginationStyle.LimitOffset, Caps.Pagination);
        Assert.False(Caps.SupportsWithTies);
        Assert.True(Caps.SupportsTryCast);
        Assert.False(Caps.SupportsLateral);                   // accepted only after the Spark proxy confirms it
        Assert.True(Caps.LimitGuaranteed);
        Assert.False(Caps.InDbHmac);                          // decision B-2
        Assert.Equal(TenantComparisonStyle.CastBinary, Caps.TenantComparison);
        Assert.Empty(Caps.AllowedTableFunctions);
    }
}

public class DatabricksCheckerTests
{
    private static BoundParameter P(int n) => new($":p{n}", $"p{n}", n - 1, "v", SqlParameterType.String, ParameterOrigin.QueryLiteral);

    private static void Check(string sql, ImmutableArray<BoundParameter>? ps = null) =>
        EmittedSqlInvariantChecker.Check(sql, TargetSqlDialect.Databricks, ps ?? ImmutableArray<BoundParameter>.Empty, Array.Empty<EmittedRange>());

    [Theory]
    [InlineData("SELECT 'a' FROM `t`")]
    [InlineData("SELECT \"a\" FROM `t`")]
    [InlineData("SELECT `a` FROM `t` -- x")]
    [InlineData("SELECT `a` FROM `t`;")]
    [InlineData("SELECT `a` FROM `t` WHERE `a` = ${x}")]
    [InlineData("SELECT `a` FROM `t` WHERE 1 = 1")]
    public void Rejects_QuotesCommentsSemicolonsSubstitutionAndNumbers(string sql) =>
        Assert.ThrowsAny<EmittedSqlInvariantViolationException>(() => Check(sql));

    [Fact]
    public void Accepts_HostileCharacters_InsideBackticks() => Check("SELECT `it's -- ; ${x} 1`, `a``b` FROM `t`");

    [Fact]
    public void ColonNamedMarkers_MapOneToOneToParameters()
    {
        Check("SELECT `a` FROM `t` WHERE `a` = :p1 AND `b` = :p2 AND `c` = :p1", new[] { P(1), P(2) }.ToImmutableArray());
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check("SELECT :p1"));
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check("SELECT `a`", new[] { P(1) }.ToImmutableArray()));
    }
}

public class DatabricksGeneratorTests
{
    private static CompiledSql Gen(string sql, IDictionary<string, PolicyValue>? values = null) =>
        CompilerTestHelpers.Generate(TargetSqlDialect.Databricks, sql, values);

    [Fact]
    public void Literals_AreBound_WithNamedColonMarkers_AndBacktickIdentifiers()
    {
        var c = Gen("SELECT id FROM t WHERE a = 'x' AND b > 5 AND c = 'x'");
        Assert.DoesNotContain('\'', c.Sql);
        Assert.Contains("`a` = :p1", c.Sql);
        Assert.Contains("`b` > :p2", c.Sql);
        Assert.Equal(2, c.Parameters.Length);
        Assert.Equal("p1", c.Parameters[0].Name);
        Assert.Equal(":p1", c.Parameters[0].Marker);
    }

    [Fact]
    public void BacktickInIdentifier_IsDoubled()
    {
        var stmt = new SelectStatement(null, new QuerySpecification(false,
            new SelectItem[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("a`b", true) })), null) },
            null, null, null, null), null, null);
        Assert.Contains("`a``b`", CompilerTestHelpers.Generate(TargetSqlDialect.Databricks, stmt).Sql);
    }

    [Theory]
    [InlineData("a$b")]
    [InlineData("a{b")]
    [InlineData("a}b")]
    [InlineData("${x}")]
    public void Databricks_IdentifierWithDollarOrBrace_Rejected(string name)
    {
        var stmt = new SelectStatement(null, new QuerySpecification(false,
            new SelectItem[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(name, true) })), null) },
            null, null, null, null), null, null);
        Assert.ThrowsAny<System.Security.SecurityException>(() => CompilerTestHelpers.Generate(TargetSqlDialect.Databricks, stmt));
    }

    [Fact]
    public void DatabricksStringLiteral_NeverEmitted_InTheLegacyPathEither()
    {
        var generator = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.Databricks);
        var stmt = CompilerTestHelpers.Build("SELECT a FROM t WHERE b = 'x'");
        Assert.Throws<InvalidOperationException>(() => generator.GenerateSql(stmt));
    }

    [Fact]
    public void ThreePartNames_AreEmittedWithAllThreeParts()
    {
        var stmt = new SelectStatement(null, new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
            new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("main", true), new SqlIdentifier("sales", true), new SqlIdentifier("orders", true) }), null),
            null, null, null), null, null);
        Assert.Contains("FROM `main`.`sales`.`orders`", CompilerTestHelpers.Generate(TargetSqlDialect.Databricks, stmt).Sql);
    }

    [Fact]
    public void Pagination_IsStructural_AndWithTiesIsRejected()
    {
        var c = Gen("SELECT id FROM t ORDER BY id OFFSET 5 LIMIT 10");
        Assert.Contains("LIMIT 10", c.Sql);
        Assert.Contains("OFFSET 5", c.Sql);
        Assert.Empty(c.Parameters);
        Assert.ThrowsAny<Exception>(() => Gen("SELECT id FROM t ORDER BY id FETCH FIRST 3 ROWS WITH TIES"));
    }

    [Theory]
    [InlineData("varchar(10)", "STRING")]
    [InlineData("varchar", "STRING")]
    [InlineData("char(3)", "STRING")]
    [InlineData("double", "DOUBLE")]
    [InlineData("real", "FLOAT")]
    [InlineData("decimal(10, 2)", "DECIMAL(10,2)")]
    [InlineData("timestamp", "TIMESTAMP_NTZ")]
    [InlineData("timestamp with time zone", "TIMESTAMP")]
    [InlineData("varbinary", "BINARY")]
    [InlineData("integer", "INT")]
    [InlineData("bigint", "BIGINT")]
    [InlineData("boolean", "BOOLEAN")]
    [InlineData("date", "DATE")]
    public void CastTypes_AreMapped(string trino, string databricks) =>
        Assert.Contains($"CAST(`a` AS {databricks})", Gen($"SELECT CAST(a AS {trino}) FROM t").Sql);

    [Fact]
    public void CastToJson_IsRejected() => Assert.ThrowsAny<Exception>(() => Gen("SELECT CAST(a AS json) FROM t"));

    [Fact]
    public void TryCast_IsNative() => Assert.Contains("TRY_CAST(`a` AS INT)", Gen("SELECT TRY_CAST(a AS integer) FROM t").Sql);

    [Theory]
    [InlineData("SELECT strpos(a, 'x') FROM t", "INSTR(`a`, :p1)")]
    [InlineData("SELECT approx_distinct(a) FROM t", "APPROX_COUNT_DISTINCT(`a`)")]
    [InlineData("SELECT arbitrary(a) FROM t", "ANY_VALUE(`a`)")]
    [InlineData("SELECT length(a) FROM t", "LENGTH(`a`)")]
    public void FunctionMapping(string sql, string expected) => Assert.Contains(expected, Gen(sql).Sql);

    [Fact]
    public void TranslatedDateFunctions_AreBound()
    {
        var stmt = new TrinoSqlEngine.Ast.Builder.SqlAstBuilder(new TrinoSqlEngine.Ast.Builder.AstBuilderOptions { TranslateTrinoDateFunctions = true })
            .BuildStatement(new FastSqlEngine().Parse("SELECT date_add('day', 3, d) AS a, date_trunc('month', d) AS t, now() AS n FROM t".AsMemory(), SqlTokenSecurityOptions.None).Tree);
        var c = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.Databricks).Generate(stmt, ParameterSource.Empty, CancellationToken.None);
        Assert.Contains("TIMESTAMPADD(DAY, :p1, `d`)", c.Sql);
        Assert.Contains("DATE_TRUNC('MONTH', `d`)", c.Sql);
        Assert.Contains("CURRENT_TIMESTAMP()", c.Sql);
        Assert.Single(c.Parameters);
    }

    [Theory]
    [InlineData("SELECT a, SUM(b) OVER (PARTITION BY a ORDER BY c ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS s FROM t")]
    [InlineData("SELECT EXTRACT(year FROM d) AS y FROM t")]
    [InlineData("SELECT a FROM t ORDER BY a DESC NULLS LAST, b ASC NULLS FIRST")]
    [InlineData("SELECT a, GROUPING(a) AS g FROM t GROUP BY ROLLUP (a)")]
    [InlineData("SELECT count(*) FILTER (WHERE a > 1) AS n FROM t")]
    [InlineData("SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS v (x, y)")]
    [InlineData("SELECT DATE '2024-01-15' AS d, TIMESTAMP '2024-01-15 10:30:00' AS ts FROM t")]
    [InlineData("SELECT a IS DISTINCT FROM b AS d FROM t")]
    [InlineData("SELECT current_date, current_timestamp FROM t")]
    [InlineData("SELECT t.a FROM t JOIN u ON t.id = u.id WHERE EXISTS (SELECT 1 FROM v WHERE v.id = t.id)")]
    [InlineData("WITH c AS (SELECT a FROM t) SELECT a FROM c UNION ALL SELECT a FROM c")]
    [InlineData("SELECT a FROM t WHERE a IN (1, 2, 3) AND b BETWEEN 1 AND 5 AND c LIKE 'a%'")]
    [InlineData("SELECT true AS a, false AS b, NULL AS c FROM t")]
    [InlineData("SELECT a || 'x' AS c, -a AS n, a % 2 AS m FROM t")]
    [InlineData("SELECT substring(a FROM 2 FOR 3) AS s, trim(BOTH 'x' FROM a) AS tr, position('a' IN b) AS p FROM t")]
    [InlineData("SELECT CASE WHEN a > 1 THEN 'x' ELSE 'y' END AS c FROM t")]
    public void DatabricksEmissionPaths_PassCheckerInBoundMode(string sql)
    {
        var c = Gen(sql);
        Assert.DoesNotContain(';', c.Sql);
    }

    [Theory]
    [InlineData("SELECT o.id FROM o CROSS JOIN LATERAL (SELECT 1 AS x FROM p WHERE p.id = o.id) l")]
    public void Lateral_IsRejected_UntilTheSparkProxyConfirmsIt(string sql)
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.Databricks);
        Assert.Throws<SqlCompileNotSupportedException>(() => DialectCapabilityValidator.Validate(CompilerTestHelpers.Build(sql), caps));
    }

    // ---- tenant predicate ----

    [Fact]
    public void TenantPredicate_ComparesBinary_ForDatabricks()
    {
        var expr = TrinoSqlEngine.Ast.Security.TenantPredicateFactory.Build(
            DialectCapabilityTable.Default.Get(TargetSqlDialect.Databricks), "tenant", "__t", SqlParameterType.String);
        var stmt = new SelectStatement(null, new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
            new NamedTableSource(new SqlQualifiedName("t"), null),
            new SecurityPredicateExpression(expr, new SecurityPredicateId("main.s.t", 0), SecurityScope.Root), null, null), null, null);
        var c = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.Databricks)
            .Generate(stmt, new ParameterSource(new Dictionary<string, PolicyValue> { ["__t"] = new("acme", SqlParameterType.String) }.ToFrozenDictionary(), new Dictionary<string, object?>()), CancellationToken.None);

        Assert.Contains("CAST(`tenant` AS BINARY) = CAST(:p1 AS BINARY)", c.Sql);
        Assert.DoesNotContain("`tenant` = :p1", c.Sql);   // a plain equality conjunct is folded into a tautology by Spark on collated columns
        Assert.Single(c.Parameters);
    }

    // ---- masks ----

    private static ColumnReference Col(string n) => new(new SqlQualifiedName(new[] { new SqlIdentifier(n, true) }));
    private static PolicyParameterExpression P(string n, SqlParameterType t) => new(n, t, ParameterOrigin.Mask);

    private static CompiledSql Mask(MaskExpression m, Dictionary<string, PolicyValue>? values = null)
    {
        var stmt = new SelectStatement(null, new QuerySpecification(false,
            new SelectItem[] { new ColumnSelectItem(m, new SqlIdentifier(m.Column.Name.SimpleName, true)) },
            new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("main", true), new SqlIdentifier("s", true), new SqlIdentifier("t", true) }), null), null, null, null), null, null);
        return CompilerTestHelpers.Generate(TargetSqlDialect.Databricks, stmt, values);
    }

    [Fact]
    public void Nullify_IsTypedNull() =>
        Assert.Contains("CAST(NULL AS STRING) AS `email`", Mask(new MaskExpression(MaskKind.Nullify, Col("email"), new MaskArguments(), "STRING")).Sql);

    [Theory]
    [InlineData("DECIMAL(18,2)")]
    [InlineData("INT")]
    [InlineData("TIMESTAMP_NTZ")]
    [InlineData("BOOLEAN")]
    public void Redact_NumericOrTemporal_IsTypedNull(string type) =>
        Assert.Contains($"CAST(NULL AS {type}) AS `x`", Mask(new MaskExpression(MaskKind.Redact, Col("x"), new MaskArguments(), type)).Sql);

    [Fact]
    public void Redact_Text_BindsTheReplacement()
    {
        var c = Mask(new MaskExpression(MaskKind.Redact, Col("email"), new MaskArguments(Constant: P("m", SqlParameterType.String)), "STRING"),
            new() { ["m"] = new("[REDACTED]", SqlParameterType.String) });
        Assert.DoesNotContain('\'', c.Sql);
        Assert.Equal("[REDACTED]", Assert.Single(c.Parameters).Value);
    }

    [Fact]
    public void PartialMask_BindsAllArguments_AndClampsCounts()
    {
        var c = Mask(new MaskExpression(MaskKind.PartialMask, Col("email"),
                new MaskArguments(KeepPrefix: P("p", SqlParameterType.Int32), KeepSuffix: P("s", SqlParameterType.Int32), MaskChar: P("c", SqlParameterType.String)), "STRING"),
            new()
            {
                ["p"] = new(2, SqlParameterType.Int32), ["s"] = new(4, SqlParameterType.Int32), ["c"] = new("*", SqlParameterType.String)
            });
        Assert.DoesNotContain('\'', c.Sql);
        Assert.Contains("GREATEST(CAST(:p1 AS INT), 0)", c.Sql);
        Assert.Equal(3, c.Parameters.Length);
    }

    [Fact]
    public void Hmac_IsNotAvailable_FailsClosed() =>
        Assert.ThrowsAny<Exception>(() => Mask(new MaskExpression(MaskKind.Hmac, Col("email"),
            new MaskArguments(HmacKey: P("k", SqlParameterType.Binary), HmacKeyOuter: P("o", SqlParameterType.Binary)), "STRING"),
            new() { ["k"] = new(new byte[] { 1 }, SqlParameterType.Binary), ["o"] = new(new byte[] { 2 }, SqlParameterType.Binary) }));

    // ---- binder, token guard ----

    [Fact]
    public void Binder_UsesNamedParameters_WithoutAColon()
    {
        var binder = new DatabricksCompiledSqlBinder();
        Assert.True(binder.CanBind(TargetSqlDialect.Databricks));
        var compiled = new CompiledSql("SELECT :p1", ImmutableArray.Create(
                new BoundParameter(":p1", "p1", 0, "acme", SqlParameterType.String, ParameterOrigin.Tenant, "t")),
            TargetSqlDialect.Databricks, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "v");
        using var cmd = new SqliteCommand();
        binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        Assert.Equal("p1", cmd.Parameters[0].ParameterName);
        Assert.Equal(DbType.String, cmd.Parameters[0].DbType);
    }

    [Theory]
    [InlineData("SELECT a FROM t WHERE b = '${x}'")]
    [InlineData("SELECT \"${x}\" FROM t")]
    [InlineData("SELECT a AS \"x${y}\" FROM t")]
    public void Databricks_TokenGuard_VariableSubstitution(string sql)
    {
        var options = SqlTokenSecurityOptions.None with { RejectVariableSubstitutionSequences = true };
        Assert.ThrowsAny<Exception>(() => new FastSqlEngine().Parse(sql.AsMemory(), options));
        new FastSqlEngine().Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);   // parses without the guard
        Assert.True(SqlTokenSecurityOptions.Strict.RejectVariableSubstitutionSequences);
    }
}
