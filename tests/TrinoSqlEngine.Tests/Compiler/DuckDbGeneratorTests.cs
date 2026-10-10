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
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

public class DuckDbCapabilityTests
{
    private static readonly DialectCapabilities Caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.DuckDb);

    [Fact]
    public void DuckDb_HasTheDocumentedValues()
    {
        Assert.Equal(DialectSupportTier.Production, Caps.Tier);
        Assert.Equal(65535, Caps.MaxBindParameters);          // conservative, verified by the in-process probe test
        Assert.Null(Caps.MaxInListItems);
        Assert.Equal('"', Caps.IdentifierOpenQuote);
        Assert.Equal('"', Caps.IdentifierCloseQuote);
        Assert.Equal(ParameterMarkerStyle.DollarOrdinal, Caps.MarkerStyle);
        Assert.True(Caps.SupportsMarkerReuse);
        Assert.Equal(PaginationStyle.LimitOffset, Caps.Pagination);
        Assert.False(Caps.SupportsWithTies);
        Assert.True(Caps.SupportsNullsFirstLast);
        Assert.Equal(BooleanRepresentation.Native, Caps.Booleans);
        Assert.True(Caps.SupportsLateral);
        Assert.True(Caps.LimitGuaranteed);
        Assert.False(Caps.InDbHmac);                           // decision B-2: HMAC degrades to Redact
        Assert.Equal(TenantComparisonStyle.EncodedBlob, Caps.TenantComparison);
        Assert.Empty(Caps.AllowedTableFunctions);
    }

    [Fact]
    public void BindExpressionTemplates_AreConstant_OnePlaceholder()
    {
        foreach (var (_, template) in Caps.BindExpressionTemplates)
        {
            Assert.Equal(1, template.Split("{0}").Length - 1);
        }
    }
}

public class DuckDbCheckerTests
{
    private static readonly ImmutableArray<BoundParameter> None = ImmutableArray<BoundParameter>.Empty;

    private static BoundParameter P(int n) => new($"${n}", $"{n}", n - 1, "v", SqlParameterType.String, ParameterOrigin.QueryLiteral);

    private static void Check(string sql, ImmutableArray<BoundParameter>? ps = null, IReadOnlyList<EmittedRange>? ranges = null) =>
        EmittedSqlInvariantChecker.Check(sql, TargetSqlDialect.DuckDb, ps ?? None, ranges ?? Array.Empty<EmittedRange>());

    [Theory]
    [InlineData("SELECT 'a' FROM \"t\"")]
    [InlineData("SELECT \"a\" FROM \"t\" -- x")]
    [InlineData("SELECT \"a\" /* x */ FROM \"t\"")]
    [InlineData("SELECT \"a\" FROM \"t\";")]
    [InlineData("SELECT $$a$$")]
    [InlineData("SELECT $tag$a$tag$")]
    [InlineData("SELECT E'a'")]
    [InlineData("SELECT \"a\" FROM \"t\" WHERE 1 = 1")]       // 1 is an unregistered numeric token
    public void Rejects_QuotesCommentsSemicolonsDollarQuotesAndUnregisteredNumbers(string sql)
    {
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(sql));
    }

    [Fact]
    public void Accepts_HostileCharacters_InsideDoubleQuotedIdentifiers()
    {
        Check("SELECT \"it's -- /* ; $$ [ ] 1\", \"a\"\"b; 2\" FROM \"t\"\"x\"");
    }

    [Fact]
    public void Rejects_UnterminatedQuotedIdentifier() =>
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check("SELECT \"abc FROM t"));

    [Fact]
    public void DollarMarkers_MapOneToOneToParameters()
    {
        Check("SELECT \"a\" FROM \"t\" WHERE \"a\" = $1 AND \"b\" = $2 AND \"c\" = $1", new[] { P(1), P(2) }.ToImmutableArray());
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check("SELECT $1"));                          // marker without parameter
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check("SELECT \"a\"", new[] { P(1) }.ToImmutableArray())); // parameter without marker
    }

    [Fact]
    public void ArraySubscript_BracketsAreAllowed_InDoubleQuoteDialects()
    {
        Check("SELECT \"a\"[$1] FROM \"t\"", new[] { P(1) }.ToImmutableArray());
    }
}

public class DuckDbGeneratorTests
{
    private static CompiledSql Gen(string sql, IDictionary<string, PolicyValue>? values = null) =>
        CompilerTestHelpers.Generate(TargetSqlDialect.DuckDb, sql, values);

    [Fact]
    public void Literals_AreBound_WithDollarOrdinalMarkers()
    {
        var c = Gen("SELECT id FROM t WHERE a = 'x' AND b > 5 AND c = 'x'");
        Assert.DoesNotContain('\'', c.Sql);
        Assert.Contains("\"a\" = $1", c.Sql);
        Assert.Contains("\"b\" > $2", c.Sql);
        Assert.Equal(2, c.Parameters.Length);                    // 'x' is deduplicated
        Assert.Equal("$1", c.Parameters[0].Marker);
        Assert.Equal("1", c.Parameters[0].Name);
    }

    [Fact]
    public void LimitAndOffset_AreStructuralIntegers()
    {
        var c = Gen("SELECT id FROM t ORDER BY id OFFSET 5 LIMIT 10");
        Assert.Contains("LIMIT 10", c.Sql);
        Assert.Contains("OFFSET 5", c.Sql);
        Assert.Empty(c.Parameters);
    }

    [Fact]
    public void WithTies_IsRejected() =>
        Assert.ThrowsAny<Exception>(() => Gen("SELECT id FROM t ORDER BY id FETCH FIRST 3 ROWS WITH TIES"));

    [Theory]
    [InlineData("SELECT a, SUM(b) OVER (PARTITION BY a ORDER BY c ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS s FROM t")]
    [InlineData("SELECT CAST(a AS decimal(10, 2)) AS d, TRY_CAST(b AS integer) AS i, CAST(c AS varchar) AS v FROM t")]
    [InlineData("SELECT EXTRACT(year FROM d) AS y, EXTRACT(dow FROM d) AS w FROM t")]
    [InlineData("SELECT a FROM t ORDER BY a DESC NULLS LAST, b ASC NULLS FIRST")]
    [InlineData("SELECT a, GROUPING(a) AS g FROM t GROUP BY ROLLUP (a)")]
    [InlineData("SELECT count(*) FILTER (WHERE a > 1) AS n FROM t")]
    [InlineData("SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS v (x, y)")]
    [InlineData("SELECT DATE '2024-01-15' AS d, TIMESTAMP '2024-01-15 10:30:00' AS ts, TIME '10:30:00' AS tm FROM t")]
    [InlineData("SELECT a IS DISTINCT FROM b AS d FROM t")]
    [InlineData("SELECT current_date, current_timestamp FROM t")]
    [InlineData("SELECT t.a FROM t JOIN u ON t.id = u.id WHERE EXISTS (SELECT 1 FROM v WHERE v.id = t.id)")]
    [InlineData("SELECT ARRAY[1, 2, 3][2] AS x FROM t")]
    [InlineData("WITH c AS (SELECT a FROM t) SELECT a FROM c UNION ALL SELECT a FROM c")]
    [InlineData("SELECT a FROM t WHERE a IN (1, 2, 3) AND b BETWEEN 1 AND 5 AND c LIKE 'a%'")]
    [InlineData("SELECT true AS a, false AS b, NULL AS c FROM t")]
    [InlineData("SELECT o.id FROM o CROSS JOIN LATERAL (SELECT 1 AS x FROM p WHERE p.id = o.id) l")]
    public void DuckDbEmissionPaths_PassCheckerInBoundMode(string sql)
    {
        var c = Gen(sql);
        Assert.DoesNotContain('\'', c.Sql);
        Assert.DoesNotContain(';', c.Sql);
    }

    [Fact]
    public void TranslatedDateFunctions_AreBound_NoQuotedIntervals()
    {
        var stmt = new TrinoSqlEngine.Ast.Builder.SqlAstBuilder(new TrinoSqlEngine.Ast.Builder.AstBuilderOptions { TranslateTrinoDateFunctions = true })
            .BuildStatement(new FastSqlEngine().Parse("SELECT date_add('day', 3, d) AS a, date_trunc('month', d) AS t, d + INTERVAL '2' DAY AS i FROM t".AsMemory(), SqlTokenSecurityOptions.None).Tree);
        var c = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.DuckDb)
            .Generate(stmt, ParameterSource.Empty, CancellationToken.None);
        Assert.Contains("INTERVAL", c.Sql);
        Assert.DoesNotContain("INTERVAL '", c.Sql);
    }

    // ---- tenant predicate ----

    [Fact]
    public void TenantPredicate_IsBlobExact_ForDuckDb()
    {
        var expr = TrinoSqlEngine.Ast.Security.TenantPredicateFactory.Build(
            DialectCapabilityTable.Default.Get(TargetSqlDialect.DuckDb), "TenantId", "__t", SqlParameterType.String);
        var stmt = new SelectStatement(null, new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
            new NamedTableSource(new SqlQualifiedName("t"), null),
            new SecurityPredicateExpression(expr, new SecurityPredicateId("main.t", 0), SecurityScope.Root), null, null), null, null);
        var c = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.DuckDb)
            .Generate(stmt, new ParameterSource(new Dictionary<string, PolicyValue> { ["__t"] = new("acme", SqlParameterType.String) }.ToFrozenDictionary(), new Dictionary<string, object?>()), CancellationToken.None);

        Assert.Contains("\"TenantId\" = $1", c.Sql);
        Assert.Contains("ENCODE(CAST(\"TenantId\" AS varchar)) = ENCODE(CAST($1 AS varchar))", c.Sql);
        Assert.Single(c.Parameters);
    }

    // ---- masks ----

    private static ColumnReference Col(string n) => new(new SqlQualifiedName(new[] { new SqlIdentifier(n, true) }));
    private static PolicyParameterExpression P(string n, SqlParameterType t) => new(n, t, ParameterOrigin.Mask);

    private static CompiledSql Mask(MaskExpression m, Dictionary<string, PolicyValue>? values = null)
    {
        var stmt = new SelectStatement(null, new QuerySpecification(false,
            new SelectItem[] { new ColumnSelectItem(m, new SqlIdentifier(m.Column.Name.SimpleName, true)) },
            new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("main", true), new SqlIdentifier("t", true) }), null), null, null, null), null, null);
        return CompilerTestHelpers.Generate(TargetSqlDialect.DuckDb, stmt, values);
    }

    [Fact]
    public void Nullify_IsTypedNull() =>
        Assert.Contains("CAST(NULL AS VARCHAR) AS \"Email\"", Mask(new MaskExpression(MaskKind.Nullify, Col("Email"), new MaskArguments(), "VARCHAR")).Sql);

    [Theory]
    [InlineData("DECIMAL(18,2)")]
    [InlineData("INTEGER")]
    [InlineData("TIMESTAMP")]
    [InlineData("BOOLEAN")]
    public void Redact_NumericOrTemporal_IsTypedNull(string type) =>
        Assert.Contains($"CAST(NULL AS {type}) AS \"X\"", Mask(new MaskExpression(MaskKind.Redact, Col("X"), new MaskArguments(), type)).Sql);

    [Fact]
    public void Redact_Text_BindsTheReplacement()
    {
        var c = Mask(new MaskExpression(MaskKind.Redact, Col("Email"), new MaskArguments(Constant: P("m", SqlParameterType.String)), "VARCHAR"),
            new() { ["m"] = new("[REDACTED]", SqlParameterType.String) });
        Assert.DoesNotContain('\'', c.Sql);
        Assert.Equal("[REDACTED]", Assert.Single(c.Parameters).Value);
    }

    [Fact]
    public void PartialMask_BindsAllArguments()
    {
        var c = Mask(new MaskExpression(MaskKind.PartialMask, Col("Email"),
                new MaskArguments(KeepPrefix: P("p", SqlParameterType.Int32), KeepSuffix: P("s", SqlParameterType.Int32), MaskChar: P("c", SqlParameterType.String)), "VARCHAR"),
            new()
            {
                ["p"] = new(2, SqlParameterType.Int32), ["s"] = new(4, SqlParameterType.Int32), ["c"] = new("*", SqlParameterType.String)
            });
        Assert.DoesNotContain('\'', c.Sql);
        Assert.Equal(3, c.Parameters.Length);
        Assert.All(c.Parameters, p => Assert.Equal(ParameterOrigin.Mask, p.Origin));
    }

    [Fact]
    public void Hmac_IsNotAvailable_FailsClosed() =>
        Assert.ThrowsAny<Exception>(() => Mask(new MaskExpression(MaskKind.Hmac, Col("Email"),
            new MaskArguments(HmacKey: P("k", SqlParameterType.Binary), HmacKeyOuter: P("o", SqlParameterType.Binary)), "VARCHAR"),
            new() { ["k"] = new(new byte[] { 1 }, SqlParameterType.Binary), ["o"] = new(new byte[] { 2 }, SqlParameterType.Binary) }));

    [Theory]
    [InlineData("VARCHAR); DROP TABLE x; --")]
    [InlineData("unknown_type")]
    [InlineData("")]
    public void HostileOrUnknownDataType_FailsClosed(string type) =>
        Assert.ThrowsAny<Exception>(() => Mask(new MaskExpression(MaskKind.Nullify, Col("X"), new MaskArguments(), type)));

    // ---- binder ----

    [Fact]
    public void Binder_UsesNumericParameterNames_AndTypedValues()
    {
        var binder = new DuckDbCompiledSqlBinder();
        Assert.True(binder.CanBind(TargetSqlDialect.DuckDb));
        Assert.False(binder.CanBind(TargetSqlDialect.SqlServer));

        var compiled = new CompiledSql("SELECT $1, $2", ImmutableArray.Create(
                new BoundParameter("$1", "1", 0, "acme", SqlParameterType.String, ParameterOrigin.Tenant, "t"),
                new BoundParameter("$2", "2", 1, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "foo")),
            TargetSqlDialect.DuckDb, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "v");
        using var cmd = new SqliteCommand();
        binder.Bind(cmd, compiled, new Dictionary<string, object?> { ["foo"] = 7L, ["1"] = "evil", ["t"] = "evil" });

        Assert.Equal("SELECT $1, $2", cmd.CommandText);
        Assert.Equal("1", cmd.Parameters[0].ParameterName);
        Assert.Equal("acme", cmd.Parameters[0].Value);
        Assert.Equal("2", cmd.Parameters[1].ParameterName);
        Assert.Equal(7L, cmd.Parameters[1].Value);
        Assert.Equal(DbType.Int64, cmd.Parameters[1].DbType);
    }

    [Fact]
    public void Binder_MissingClientValue_FailsClosed()
    {
        var binder = new DuckDbCompiledSqlBinder();
        var compiled = new CompiledSql("SELECT $1", ImmutableArray.Create(
                new BoundParameter("$1", "1", 0, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "foo")),
            TargetSqlDialect.DuckDb, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "v");
        using var cmd = new SqliteCommand();
        Assert.ThrowsAny<System.Security.SecurityException>(() => binder.Bind(cmd, compiled, new Dictionary<string, object?>()));
    }
}
