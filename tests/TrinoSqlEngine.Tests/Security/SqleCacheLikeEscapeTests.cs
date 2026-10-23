using System.Collections.Immutable;
using System.Security;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>SQLE-10: the LIKE escaping of a policy parameter survives a template cache hit (rebind).</summary>
[Trait("Category", "Security")]
public class SqleCacheLikeEscapeTests
{
    private static SelectStatement LikeStatement(Expression pattern) =>
        new(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName("t"), null),
                new LikeExpression(new ColumnReference(new SqlQualifiedName("n")), pattern), null, null), null, null);

    private static SelectStatement EqualStatement() =>
        new(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName("t"), null),
                new BinaryExpression(new ColumnReference(new SqlQualifiedName("n")), BinaryOperator.Equal,
                    new PolicyParameterExpression("pat", SqlParameterType.String)), null, null), null, null);

    private static IReadOnlyDictionary<string, PolicyValue> Values(object? value) =>
        new Dictionary<string, PolicyValue> { ["pat"] = new(value, SqlParameterType.String) };

    private static CompiledSqlTemplate Template(SelectStatement statement, object? firstValue) =>
        CompiledSqlTemplate.From("key", CompilerTestHelpers.GenerateSqlServer(statement, new Dictionary<string, PolicyValue>(Values(firstValue))),
            ImmutableArray<TableDependency>.Empty);

    private static SelectStatement PolicyLike() => LikeStatement(new PolicyParameterExpression("pat", SqlParameterType.String));

    [Fact]
    public void PolicyLikeParameter_CacheHit_BindsEscapedValue()
    {
        var template = Template(PolicyLike(), "sales_eu%");

        var miss = CompilerTestHelpers.GenerateSqlServer(PolicyLike(), new Dictionary<string, PolicyValue>(Values("sales_eu%")));
        var hit = template.Rebind(Values("sales_eu%"));

        Assert.Equal("sales\\_eu\\%", Assert.Single(miss.Parameters).Value);
        Assert.Equal("sales\\_eu\\%", Assert.Single(hit.Parameters).Value);
    }

    [Fact]
    public void PolicyLikeParameter_CacheHit_DifferentUserValue_IsEscaped()
    {
        var template = Template(PolicyLike(), "plain");

        var hit = template.Rebind(Values("50%_[x]\\"));

        Assert.Equal("50\\%\\_\\[x]\\\\", Assert.Single(hit.Parameters).Value);
    }

    [Fact]
    public void PolicyLikeParameter_CacheHit_NonStringValue_Throws()
    {
        var template = Template(PolicyLike(), "plain");

        Assert.Throws<SecurityException>(() => template.Rebind(new Dictionary<string, PolicyValue> { ["pat"] = new(42, SqlParameterType.String) }));
    }

    [Fact]
    public void Rebind_SlotWithoutEscapeFlag_BindsRaw()
    {
        var template = Template(EqualStatement(), "a");

        var hit = template.Rebind(Values("50%_"));

        Assert.Equal("50%_", Assert.Single(hit.Parameters).Value);
    }
}
