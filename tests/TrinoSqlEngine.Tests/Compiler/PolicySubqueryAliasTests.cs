using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>CR-ADG-16: the implicit alias of a policy-subquery table keeps the user's quoting, like <c>SecureTyped</c>.</summary>
public class PolicySubqueryAliasTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();

    private static ColumnReference Col(params (string Name, bool Quoted)[] parts) =>
        new(new SqlQualifiedName(parts.Select(p => new SqlIdentifier(p.Name, p.Quoted)).ToList()));

    private static PolicyPredicate UnaliasedCorrelatedExists(bool quotedTableName) => PolicyPredicate.Create(
        new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(Col(("Entitlements", quotedTableName), ("Id", false)), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Entitlements", quotedTableName) }), null),
                new BinaryExpression(Col(("Entitlements", quotedTableName), ("OrderId", false)), BinaryOperator.Equal, Col(("autheris_target", false), ("Id", false))),
                null, null), null, null)),
        new Dictionary<string, PolicyValue>());

    private CompiledSql Compile(TargetSqlDialect dialect)
    {
        return _engine.Compile("SELECT id FROM orders".AsMemory(), new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = _rowFilters,
                Masks = new DictMaskProvider(),
                Catalog = Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        }, CancellationToken.None);
    }

    [Fact]
    public void UnquotedUserName_GivesAnUnquotedAlias_SoOracleFoldsTheAliasAndTheReferenceTheSameWay()
    {
        _rowFilters.Predicates[Orders] = UnaliasedCorrelatedExists(quotedTableName: false);
        var c = Compile(TargetSqlDialect.Oracle);

        // Oracle upper-cases unquoted names: alias and the correlated references fold identically. A delimited mixed-case alias
        // ("Entitlements") would not match the folded reference ("ENTITLEMENTS"."ID") and fail with ORA-00904.
        Assert.Contains("\"ENTITLEMENTS\"", c.Sql);
        Assert.DoesNotContain("\"Entitlements\" ", c.Sql.Replace("\"dbo\".\"Entitlements\"", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void QuotedUserName_KeepsAQuotedAlias()
    {
        _rowFilters.Predicates[Orders] = UnaliasedCorrelatedExists(quotedTableName: true);
        var c = Compile(TargetSqlDialect.Oracle);
        Assert.Contains(") \"Entitlements\"", c.Sql);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.DuckDb)]
    public void OtherDialects_CompileBothSpellings(TargetSqlDialect dialect)
    {
        _rowFilters.Predicates[Orders] = UnaliasedCorrelatedExists(quotedTableName: false);
        Assert.NotEmpty(Compile(dialect).Sql);
        _rowFilters.Predicates[Orders] = UnaliasedCorrelatedExists(quotedTableName: true);
        Assert.NotEmpty(Compile(dialect).Sql);
    }
}
