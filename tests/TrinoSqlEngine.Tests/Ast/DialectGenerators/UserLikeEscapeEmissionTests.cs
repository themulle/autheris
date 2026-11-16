namespace TrinoSqlEngine.Tests.Ast.DialectGenerators;

using System;
using System.Collections.Immutable;
using System.Threading;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

[Trait("Category", "Security")]
public sealed class UserLikeEscapeEmissionTests
{
    private static readonly TableIdentity Orders = new("dbo", "Orders");

    private static readonly InMemoryTableCatalog Catalog = new(new[]
    {
        new TableCatalogEntry(
            Orders,
            ImmutableArray.Create(
                new CatalogColumn("Id", "int"),
                new CatalogColumn("TenantId", "nvarchar(64)"),
                new CatalogColumn("Email", "nvarchar(200)"),
                new CatalogColumn("Name", "nvarchar(200)")),
            "TenantId",
            1)
    }, "dbo");

    private static CompiledSql Compile(
        string sql,
        TargetSqlDialect dialect,
        FastSqlEngine? engine = null,
        DictPolicyProvider? rowFilters = null) =>
        (engine ?? new FastSqlEngine()).Compile(
            sql.AsMemory(),
            new CompileRequest
            {
                TargetDialect = dialect,
                TokenGuards = SqlTokenSecurityOptions.Strict with
                {
                    RejectBackslashInStrings = false,
                    RejectBracketLexerDifferentials = false
                },
                Policy = new GovernancePolicy
                {
                    RowFilters = rowFilters ?? new DictPolicyProvider(),
                    Masks = new DictMaskProvider(),
                    Catalog = Catalog,
                    Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
                }
            },
            CancellationToken.None);

    [Fact]
    public void SqlServer_BracketInLiteralPattern_EscapesBracket_EmitsEscapeBackslash_AndBindsPattern()
    {
        var compiled = Compile("SELECT id FROM orders WHERE name LIKE 'a[b]%_c'", TargetSqlDialect.SqlServer);

        Assert.Contains("ESCAPE '\\'", compiled.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("a[b]", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "a\\[b]%_c"));
    }

    [Fact]
    public void SqlServer_CustomEscapeChar_EscapesBracketsWithoutDoubleEscaping()
    {
        var compiled = Compile(
            "SELECT id FROM orders WHERE name LIKE 'a[b]!%c![d]!!' ESCAPE '!'",
            TargetSqlDialect.SqlServer);

        Assert.Contains("ESCAPE ", compiled.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("a[b]", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "a![b]!%c![d]!!"));
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "!"));
    }

    [Theory]
    [InlineData("SELECT id FROM orders WHERE name LIKE 'a%' ESCAPE 'ab'")]
    [InlineData("SELECT id FROM orders WHERE name LIKE 'a%' ESCAPE ''")]
    [InlineData("SELECT id FROM orders WHERE name LIKE 'a%' ESCAPE '%'")]
    [InlineData("SELECT id FROM orders WHERE name LIKE 'a%' ESCAPE '_'")]
    [InlineData("SELECT id FROM orders WHERE name LIKE 'a%' ESCAPE '\u0001'")]
    [InlineData("SELECT id FROM orders WHERE name LIKE 'a%' ESCAPE email")]
    public void UserEscape_NonSinglePrintableAsciiOrWildcard_IsRejected(string sql)
    {
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(sql, TargetSqlDialect.PostgreSql));
        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
    }

    [Theory]
    [InlineData("SELECT id FROM orders WHERE name LIKE email")]
    [InlineData("SELECT id FROM orders WHERE name LIKE concat('a', '%')")]
    public void SqlServer_NonLiteralPattern_IsRejected(string sql)
    {
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(sql, TargetSqlDialect.SqlServer));
        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Databricks)]
    public void PostgreSql_And_Databricks_EmitExplicitEscape_ForLiteralAndNonLiteralPatterns(TargetSqlDialect dialect)
    {
        var literal = Compile("SELECT id FROM orders WHERE name LIKE 'a\\b%'", dialect);
        // Spark interprets backslash escapes inside quoted literals, so Databricks needs the doubled form and has no "no escape" spelling.
        var backslashEscape = dialect == TargetSqlDialect.Databricks ? "ESCAPE '\\\\'" : "ESCAPE '\\'";
        Assert.Contains(backslashEscape, literal.Sql, StringComparison.Ordinal);
        Assert.Contains(literal.Parameters, p => Equals(p.Value, "a\\\\b%"));

        var nonLiteral = Compile("SELECT id FROM orders WHERE name LIKE email", dialect);
        Assert.Contains(dialect == TargetSqlDialect.Databricks ? backslashEscape : "ESCAPE ''", nonLiteral.Sql, StringComparison.Ordinal);

        var custom = Compile("SELECT id FROM orders WHERE name LIKE 'a\\b%' ESCAPE '!'", dialect);
        Assert.Contains(custom.Parameters, p => Equals(p.Value, "a\\b%"));
        Assert.Contains(custom.Parameters, p => Equals(p.Value, "!"));
    }

    [Theory]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.DuckDb)]
    public void Oracle_DuckDb_WithoutExplicitEscape_RemainsUntouched(TargetSqlDialect dialect)
    {
        var compiled = Compile("SELECT id FROM orders WHERE name LIKE 'a[b]%'", dialect);

        Assert.DoesNotContain("ESCAPE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "a[b]%"));
    }

    [Fact]
    public void RewrittenPattern_ExceedingMaxQueryLength_ThrowsSqlLimitExceeded()
    {
        string brackets = new string('[', 60);
        string sql = $"SELECT id FROM orders WHERE name LIKE '{brackets}'";
        var engine = new FastSqlEngine { MaxQueryLength = sql.Length + 10 };

        var ex = Assert.Throws<SqlLimitExceededException>(() => Compile(sql, TargetSqlDialect.SqlServer, engine));
        Assert.Equal(SqlLimitKind.QueryLength, ex.Kind);
    }

    [Fact]
    public void SqlServer_RepeatedCompileCacheHit_DoesNotDoubleEscapePattern()
    {
        var engine = new FastSqlEngine();
        const string sql = "SELECT id FROM orders WHERE name LIKE 'a[b]%'";

        var first = Compile(sql, TargetSqlDialect.SqlServer, engine);
        var second = Compile(sql, TargetSqlDialect.SqlServer, engine);

        Assert.Contains(first.Parameters, p => Equals(p.Value, "a\\[b]%"));
        Assert.Contains(second.Parameters, p => Equals(p.Value, "a\\[b]%"));
    }

    [Fact]
    public void PolicyLike_WithUserLikePresent_StillEscapesDeclaredValuesAndEmitsEscape()
    {
        var time = new FakeTimeProvider();
        var builder = Context(Orders);
        builder.Parameters["org"] = new PolicyValue("sales_eu", SqlParameterType.String);
        var pred = new PolicyExpressionParser(new FastSqlEngine(), time, null).Parse("email LIKE :org || '%'", builder.Build());

        var compiled = CompilerTestHelpers.Generate(
            TargetSqlDialect.SqlServer,
            new SelectStatement(
                null,
                new QuerySpecification(
                    false,
                    new SelectItem[] { new WildcardSelectItem(null) },
                    new NamedTableSource(new SqlQualifiedName("orders"), null),
                    pred.Expression,
                    null,
                    null),
                null,
                null),
            pred.Parameters.ToDictionary(x => x.Key, x => x.Value));

        Assert.Contains("ESCAPE '\\'", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "sales\\_eu"));
    }
}
