using System.Text;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

public class ParameterAccountingTests
{
    private static string InListQuery(int count)
    {
        var sb = new StringBuilder("SELECT id FROM t WHERE a IN (");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append("__param_x").Append(i);
        }
        return sb.Append(')').ToString();
    }

    [Fact]
    public void ClientNamedParameter_IsBoundWithGeneratedMarker()
    {
        var compiled = CompilerTestHelpers.GenerateSqlServer("SELECT id FROM t WHERE a = __param_foo");

        Assert.Contains("[a] = @p0", compiled.Sql);
        var p = Assert.Single(compiled.Parameters);
        Assert.Equal("@p0", p.Marker);
        Assert.Equal(ParameterOrigin.ClientNamed, p.Origin);
        Assert.Equal("foo", p.SourceName);
        Assert.Equal(0, p.Ordinal);
        Assert.Equal(TargetSqlDialect.SqlServer, compiled.Dialect);
        Assert.Equal(SqlStatementClass.Select, compiled.StatementClass);
        Assert.False(string.IsNullOrEmpty(compiled.CompilerVersion));
    }

    [Fact]
    public void SameClientNameTwice_ReusesOneMarker()
    {
        var compiled = CompilerTestHelpers.GenerateSqlServer("SELECT id FROM t WHERE a = __param_foo OR b = __param_foo");

        Assert.Single(compiled.Parameters);
        Assert.Equal(2, CountOf(compiled.Sql, "@p0"));
    }

    [Fact]
    public void BindLimit_AtMaximumPasses_OneOverThrowsTypedException()
    {
        var ok = CompilerTestHelpers.GenerateSqlServer(InListQuery(2100));
        Assert.Equal(2100, ok.Parameters.Length);

        var ex = Assert.Throws<SqlLimitExceededException>(() => CompilerTestHelpers.GenerateSqlServer(InListQuery(2101)));
        Assert.Equal(SqlLimitKind.BindParameters, ex.Kind);
        Assert.Equal(TargetSqlDialect.SqlServer, ex.Dialect);
        Assert.Equal(2101, ex.Requested);
        Assert.Equal(2100, ex.Maximum);
    }

    [Theory]
    [InlineData("p1")]
    [InlineData("P12")]
    [InlineData("__x")]
    [InlineData("gql_limit")]
    [InlineData("autheris_tenant")]
    [InlineData("Autheris_x")]
    public void ClientParameterName_WithReservedPrefix_IsRejected(string name)
    {
        Assert.Throws<System.Security.SecurityException>(() =>
            CompilerTestHelpers.GenerateSqlServer($"SELECT id FROM t WHERE a = __param_{name}"));
    }

    [Fact]
    public void PositionalClientParameter_IsRejected_FailClosed()
    {
        Assert.Throws<System.Security.SecurityException>(() => CompilerTestHelpers.GenerateSqlServer("SELECT id FROM t WHERE a = ?"));
    }

    [Fact]
    public void PolicyParameter_IsBoundFromParameterSource_WithOrigin()
    {
        var stmt = new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName("t"), null),
                new BinaryExpression(
                    new ColumnReference(new SqlQualifiedName("tenant")),
                    BinaryOperator.Equal,
                    new PolicyParameterExpression("__autheris_tenant", SqlParameterType.String, ParameterOrigin.Tenant)),
                null, null),
            null, null);

        var compiled = CompilerTestHelpers.GenerateSqlServer(stmt,
            new Dictionary<string, PolicyValue> { ["__autheris_tenant"] = new("acme", SqlParameterType.String) });

        var p = Assert.Single(compiled.Parameters);
        Assert.Equal("acme", p.Value);
        Assert.Equal(ParameterOrigin.Tenant, p.Origin);
        Assert.Equal("__autheris_tenant", p.SourceName);
    }

    [Fact]
    public void PolicyParameter_WithoutValue_FailsClosed()
    {
        var stmt = new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName("t"), null),
                new BinaryExpression(
                    new ColumnReference(new SqlQualifiedName("tenant")),
                    BinaryOperator.Equal,
                    new PolicyParameterExpression("missing", SqlParameterType.String)),
                null, null),
            null, null);

        Assert.Throws<System.Security.SecurityException>(() => CompilerTestHelpers.GenerateSqlServer(stmt));
    }

    [Fact]
    public void DeepOrChain_IsRejectedWithTypedError_NoCrash()
    {
        // SEC-ADG-05: a very deep tree (built by hand, the parser caps its own depth) must not overflow the stack.
        Expression deep = new LiteralExpression(1L, LiteralType.Integer);
        for (int i = 0; i < 200_000; i++)
        {
            deep = new BinaryExpression(deep, BinaryOperator.Or, new ColumnReference(new SqlQualifiedName("a")));
        }

        var stmt = new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName("t"), null), deep, null, null),
            null, null);
        var ex = Assert.Throws<SqlLimitExceededException>(() => CompilerTestHelpers.GenerateSqlServer(stmt));
        Assert.Equal(SqlLimitKind.NestingDepth, ex.Kind);
    }

    [Fact]
    public void CancelledToken_AbortsGeneration()
    {
        var generator = TrinoSqlEngine.Ast.Generators.SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.SqlServer);
        var stmt = CompilerTestHelpers.Build(InListQuery(1000));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            generator.Generate(stmt, new ParameterSource(System.Collections.Frozen.FrozenDictionary<string, PolicyValue>.Empty, new Dictionary<string, object?>()), cts.Token));
    }

    [Fact]
    public void Generate_ForUnsupportedDialect_FailsClosed()
    {
        var generator = TrinoSqlEngine.Ast.Generators.SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.Oracle);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            generator.Generate(CompilerTestHelpers.Build("SELECT 1"), new ParameterSource(System.Collections.Frozen.FrozenDictionary<string, PolicyValue>.Empty, new Dictionary<string, object?>()), CancellationToken.None));
    }

    private static int CountOf(string s, string needle)
    {
        int c = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { c++; i += needle.Length; }
        return c;
    }
}
