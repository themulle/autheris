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

public class PostgreSqlCapabilityTests
{
    private static readonly DialectCapabilities Caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql);

    [Fact]
    public void PostgreSql_HasTheDocumentedValues()
    {
        Assert.Equal(DialectSupportTier.Production, Caps.Tier);
        Assert.Equal(65535, Caps.MaxBindParameters);                 // Int16 parameter count in the Bind message [PG-PROTO]
        Assert.Null(Caps.MaxInListItems);
        Assert.Equal(63, Caps.MaxIdentifierLength);                  // NAMEDATALEN - 1, silently truncated by the server [PG-LEX]
        Assert.Equal(IdentifierLengthUnit.Bytes, Caps.IdentifierLengthUnit);
        Assert.Equal('"', Caps.IdentifierOpenQuote);
        Assert.Equal(ParameterMarkerStyle.DollarOrdinal, Caps.MarkerStyle);
        Assert.True(Caps.SupportsMarkerReuse);
        Assert.Equal(PaginationStyle.LimitOffset, Caps.Pagination);
        Assert.True(Caps.SupportsWithTies);
        Assert.True(Caps.SupportsNullsFirstLast);
        Assert.False(Caps.SupportsTryCast);
        Assert.True(Caps.SupportsLateral);
        Assert.True(Caps.LimitGuaranteed);
        Assert.True(Caps.InDbHmac);                                  // pgcrypto hmac(); a missing extension fails at execution (closed)
        Assert.Equal(TenantComparisonStyle.TextSendBytea, Caps.TenantComparison);
        Assert.Empty(Caps.AllowedTableFunctions);
    }
}

public class PostgreSqlGeneratorTests
{
    private static CompiledSql Gen(string sql, IDictionary<string, PolicyValue>? values = null) =>
        CompilerTestHelpers.Generate(TargetSqlDialect.PostgreSql, sql, values);

    [Fact]
    public void Literals_AreBound_WithDollarOrdinalMarkers_AndDoubleQuotedIdentifiers()
    {
        var c = Gen("SELECT id FROM t WHERE a = 'x' AND b > 5 AND c = 'x'");
        Assert.DoesNotContain('\'', c.Sql);
        Assert.Contains("\"a\" = $1", c.Sql);
        Assert.Contains("\"b\" > $2", c.Sql);
        Assert.Equal(2, c.Parameters.Length);
        Assert.Equal("1", c.Parameters[0].Name);
    }

    [Fact]
    public void Pagination_IsStructural_IncludingWithTies()
    {
        Assert.Empty(Gen("SELECT id FROM t ORDER BY id OFFSET 5 LIMIT 10").Parameters);
        var ties = Gen("SELECT id FROM t ORDER BY id FETCH FIRST 3 ROWS WITH TIES");
        Assert.Contains("FETCH FIRST 3 ROWS WITH TIES", ties.Sql);
        Assert.Empty(ties.Parameters);
    }

    [Fact]
    public void TryCast_IsRejected_PostgreSqlHasNone() =>
        Assert.ThrowsAny<Exception>(() => Gen("SELECT TRY_CAST(a AS integer) FROM t"));

    [Theory]
    [InlineData("SELECT a, SUM(b) OVER (PARTITION BY a ORDER BY c ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS s FROM t")]
    [InlineData("SELECT CAST(a AS decimal(10, 2)) AS d, CAST(b AS double) AS i, CAST(c AS varchar) AS v, CAST(d AS varbinary) AS x FROM t")]
    [InlineData("SELECT EXTRACT(year FROM d) AS y, EXTRACT(dow FROM d) AS w FROM t")]
    [InlineData("SELECT a FROM t ORDER BY a DESC NULLS LAST, b ASC NULLS FIRST")]
    [InlineData("SELECT a, GROUPING(a) AS g FROM t GROUP BY ROLLUP (a)")]
    [InlineData("SELECT count(*) FILTER (WHERE a > 1) AS n, array_agg(a ORDER BY b) AS xs FROM t")]
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
    [InlineData("SELECT a || 'x' AS c, -a AS n, a % 2 AS m FROM t")]
    public void PostgreSqlEmissionPaths_PassCheckerInBoundMode(string sql)
    {
        var c = Gen(sql);
        Assert.DoesNotContain('\'', c.Sql.Replace("'1 day'", string.Empty).Replace("'day'", string.Empty));
        Assert.DoesNotContain(';', c.Sql);
    }

    [Fact]
    public void TranslatedDateFunctions_AreBound_WithReviewedUnitFragments()
    {
        var stmt = new TrinoSqlEngine.Ast.Builder.SqlAstBuilder(new TrinoSqlEngine.Ast.Builder.AstBuilderOptions { TranslateTrinoDateFunctions = true })
            .BuildStatement(new FastSqlEngine().Parse("SELECT date_add('day', 3, d) AS a, date_trunc('month', d) AS t, d + INTERVAL '2' DAY AS i FROM t".AsMemory(), SqlTokenSecurityOptions.None).Tree);
        var c = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.PostgreSql).Generate(stmt, ParameterSource.Empty, CancellationToken.None);
        Assert.Contains("CAST($1 AS bigint) * INTERVAL '1 day'", c.Sql);
        Assert.Contains("DATE_TRUNC('month', ", c.Sql);
        Assert.Equal(2, c.Parameters.Length);   // the day amounts 3 and 2 are bound
    }

    [Fact]
    public void IdentifierOver63Bytes_IsATypedRejection_NotSilentlyTruncated()
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql);
        string name = new string('é', 40);          // 40 characters, 80 UTF-8 bytes
        var stmt = new SelectStatement(null, new QuerySpecification(false,
            new SelectItem[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("a", true) })), new SqlIdentifier(name, true)) },
            null, null, null, null), null, null);
        var ex = Assert.Throws<SqlLimitExceededException>(() => DialectCapabilityValidator.Validate(stmt, caps));
        Assert.Equal(SqlLimitKind.IdentifierLength, ex.Kind);
        Assert.Equal(80, ex.Requested);
        DialectCapabilityValidator.Validate(new SelectStatement(null, new QuerySpecification(false,
            new SelectItem[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("a", true) })), new SqlIdentifier(new string('x', 63), true)) },
            null, null, null, null), null, null), caps);
    }

    // ---- tenant predicate ----

    [Fact]
    public void TenantPredicate_ComparesTheRawBytes_UnderAnyCollation()
    {
        var expr = TrinoSqlEngine.Ast.Security.TenantPredicateFactory.Build(
            DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql), "tenantid", "__t", SqlParameterType.String);
        var stmt = new SelectStatement(null, new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
            new NamedTableSource(new SqlQualifiedName("t"), null),
            new SecurityPredicateExpression(expr, new SecurityPredicateId("public.t", 0), SecurityScope.Root), null, null), null, null);
        var c = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.PostgreSql)
            .Generate(stmt, new ParameterSource(new Dictionary<string, PolicyValue> { ["__t"] = new("acme", SqlParameterType.String) }.ToFrozenDictionary(), new Dictionary<string, object?>()), CancellationToken.None);

        Assert.Contains("\"tenantid\" = $1", c.Sql);
        Assert.Contains("TEXTSEND(CAST(\"tenantid\" AS text)) = TEXTSEND(CAST($1 AS text))", c.Sql);
        Assert.Single(c.Parameters);
    }

    // ---- masks ----

    private static ColumnReference Col(string n) => new(new SqlQualifiedName(new[] { new SqlIdentifier(n, true) }));
    private static PolicyParameterExpression P(string n, SqlParameterType t) => new(n, t, ParameterOrigin.Mask);

    private static CompiledSql Mask(MaskExpression m, Dictionary<string, PolicyValue>? values = null)
    {
        var stmt = new SelectStatement(null, new QuerySpecification(false,
            new SelectItem[] { new ColumnSelectItem(m, new SqlIdentifier(m.Column.Name.SimpleName, true)) },
            new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("public", true), new SqlIdentifier("t", true) }), null), null, null, null), null, null);
        return CompilerTestHelpers.Generate(TargetSqlDialect.PostgreSql, stmt, values);
    }

    [Fact]
    public void Nullify_IsTypedNull() =>
        Assert.Contains("CAST(NULL AS varchar(200)) AS \"email\"", Mask(new MaskExpression(MaskKind.Nullify, Col("email"), new MaskArguments(), "varchar(200)")).Sql);

    [Theory]
    [InlineData("numeric(18,2)")]
    [InlineData("integer")]
    [InlineData("timestamp with time zone")]
    [InlineData("double precision")]
    [InlineData("boolean")]
    public void Redact_NumericOrTemporal_IsTypedNull(string type) =>
        Assert.Contains($"CAST(NULL AS {type}) AS \"x\"", Mask(new MaskExpression(MaskKind.Redact, Col("x"), new MaskArguments(), type)).Sql);

    [Fact]
    public void Redact_Text_BindsTheReplacement()
    {
        var c = Mask(new MaskExpression(MaskKind.Redact, Col("email"), new MaskArguments(Constant: P("m", SqlParameterType.String)), "text"),
            new() { ["m"] = new("[REDACTED]", SqlParameterType.String) });
        Assert.DoesNotContain('\'', c.Sql);
        Assert.Equal("[REDACTED]", Assert.Single(c.Parameters).Value);
    }

    [Fact]
    public void PartialMask_BindsAllArguments()
    {
        var c = Mask(new MaskExpression(MaskKind.PartialMask, Col("email"),
                new MaskArguments(KeepPrefix: P("p", SqlParameterType.Int32), KeepSuffix: P("s", SqlParameterType.Int32), MaskChar: P("c", SqlParameterType.String)), "text"),
            new()
            {
                ["p"] = new(2, SqlParameterType.Int32), ["s"] = new(4, SqlParameterType.Int32), ["c"] = new("*", SqlParameterType.String)
            });
        Assert.DoesNotContain('\'', c.Sql);
        Assert.Equal(3, c.Parameters.Length);
    }

    [Fact]
    public void Hmac_UsesPgcrypto_WithBoundKey_AndReviewedConstantFragments()
    {
        var c = Mask(new MaskExpression(MaskKind.Hmac, Col("email"), new MaskArguments(HmacKey: P("k", SqlParameterType.String)), "text"),
            new() { ["k"] = new("00ff", SqlParameterType.String) });
        Assert.Contains("ENCODE(HMAC(CAST(\"email\" AS text), CAST($1 AS text), 'sha256'), 'hex')", c.Sql);
        Assert.Equal("00ff", Assert.Single(c.Parameters).Value);
    }

    [Theory]
    [InlineData("text); DROP TABLE x; --")]
    [InlineData("unknown_type")]
    [InlineData("")]
    public void HostileOrUnknownDataType_FailsClosed(string type) =>
        Assert.ThrowsAny<Exception>(() => Mask(new MaskExpression(MaskKind.Nullify, Col("x"), new MaskArguments(), type)));

    // ---- binder ----

    [Fact]
    public void Binder_UsesPositionalUnnamedParameters_InOrdinalOrder()
    {
        var binder = new PostgreSqlCompiledSqlBinder();
        Assert.True(binder.CanBind(TargetSqlDialect.PostgreSql));
        Assert.False(binder.CanBind(TargetSqlDialect.DuckDb));

        var compiled = new CompiledSql("SELECT $1, $2", ImmutableArray.Create(
                new BoundParameter("$1", "1", 0, "acme", SqlParameterType.String, ParameterOrigin.Tenant, "t"),
                new BoundParameter("$2", "2", 1, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "foo")),
            TargetSqlDialect.PostgreSql, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "v");
        using var cmd = new SqliteCommand();
        binder.Bind(cmd, compiled, new Dictionary<string, object?> { ["foo"] = 7, ["1"] = "evil" });

        Assert.Equal("SELECT $1, $2", cmd.CommandText);
        Assert.Equal(string.Empty, cmd.Parameters[0].ParameterName);
        Assert.Equal("acme", cmd.Parameters[0].Value);
        Assert.Equal(7, cmd.Parameters[1].Value);
        Assert.Equal(DbType.Int32, cmd.Parameters[1].DbType);
    }

    [Fact]
    public void Binder_RejectsNulInStrings_PostgreSqlCannotStoreIt()
    {
        var binder = new PostgreSqlCompiledSqlBinder();
        var compiled = new CompiledSql("SELECT $1", ImmutableArray.Create(
                new BoundParameter("$1", "1", 0, "a\0b", SqlParameterType.String, ParameterOrigin.QueryLiteral)),
            TargetSqlDialect.PostgreSql, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "v");
        using var cmd = new SqliteCommand();
        Assert.ThrowsAny<System.Security.SecurityException>(() => binder.Bind(cmd, compiled, new Dictionary<string, object?>()));
    }
}
