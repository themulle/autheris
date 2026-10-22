using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>SQLE-09: a policy LIKE pattern may combine literals and declared parameters with ||; every declared value is escaped.</summary>
[Trait("Category", "Security")]
public class SqlePolicyLikeConcatTests
{
    private readonly FakeTimeProvider _time = new();

    private PolicyPredicate Parse(string text)
    {
        var builder = Context(Orders);
        builder.Parameters["org"] = new PolicyValue("sales_eu", SqlParameterType.String);
        return new PolicyExpressionParser(new FastSqlEngine(), _time, null).Parse(text, builder.Build());
    }

    private static SelectStatement Where(Expression predicate) =>
        new(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName("t"), null), predicate, null, null), null, null);

    private CompiledSql Emit(TargetSqlDialect dialect, PolicyPredicate predicate) =>
        CompilerTestHelpers.Generate(dialect, Where(predicate.Expression), predicate.Parameters.ToDictionary(x => x.Key, x => x.Value));

    [Fact]
    public void PolicyLike_ConcatPrefix_EscapesDeclaredValue_KeepsLiteralWildcard()
    {
        var compiled = Emit(TargetSqlDialect.PostgreSql, Parse("email LIKE :org || '%'"));

        Assert.Contains("LIKE ($1 || $2) ESCAPE '\\'", compiled.Sql);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "sales\\_eu"));
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "%"));
    }

    [Fact]
    public void PolicyLike_ConcatPrefix_SqlServer_EmitsPlusAndEscape()
    {
        var compiled = Emit(TargetSqlDialect.SqlServer, Parse("email LIKE :org || '%'"));

        Assert.Contains(" + ", compiled.Sql);
        Assert.Contains("ESCAPE '\\'", compiled.Sql);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "sales\\_eu"));
    }

    [Fact]
    public void PolicyLike_ConcatPrefix_Sqlite_Rejected()
    {
        var predicate = Parse("email LIKE :org || '%'");
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.Sqlite);

        Assert.Throws<SqlCompileNotSupportedException>(() => DialectCapabilityValidator.Validate(Where(predicate.Expression), caps));
    }

    [Theory]
    [InlineData("email LIKE UPPER(:org)")]
    [InlineData("email LIKE CASE WHEN region = 'a' THEN :org ELSE '%' END")]
    [InlineData("email LIKE name")]
    public void PolicyLike_UnsupportedPatternShape_Rejected(string policy)
    {
        Assert.Throws<PolicyParseException>(() => Parse(policy));
    }

    [Fact]
    public void PolicyLike_ConcatPrefix_CacheHit_StillEscaped()
    {
        var predicate = Parse("email LIKE :org || '%'");
        var template = CompiledSqlTemplate.From("key", Emit(TargetSqlDialect.PostgreSql, predicate), ImmutableArray<TableDependency>.Empty);
        var values = predicate.Parameters.ToDictionary(x => x.Key, x => x.Value);
        values["org"] = new PolicyValue("a%_b", SqlParameterType.String);

        var hit = template.Rebind(values);

        Assert.Contains(hit.Parameters, p => Equals(p.Value, "a\\%\\_b"));
        Assert.Contains(hit.Parameters, p => Equals(p.Value, "%"));
    }
}
