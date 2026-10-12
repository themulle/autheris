using System.Collections.Immutable;
using System.Security;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// WP-X1 (found by the first end-to-end run of a virtual filter on the typed path): the typed builders emit the constant projection of an
/// EXISTS (<c>EXISTS (SELECT 1 FROM dep WHERE dep.id = autheris_target.id)</c>), which is not a value of the row policy and cannot decide
/// the outcome. The coverage verifier accepts exactly that constant (the integer 1 as an EXISTS projection) and still rejects every other raw
/// literal in an injected predicate (INV-5).
/// </summary>
public class ExistsProjectionCoverageTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    private static ColumnReference Col(params string[] parts) => new(new SqlQualifiedName(parts.Select(p => new SqlIdentifier(p, true)).ToArray()));

    private static PolicyPredicate Exists(Expression projection, Expression? extraWhere = null)
    {
        Expression where = new BinaryExpression(Col("e", "OrderId"), BinaryOperator.Equal, Col(RowFilterAliases.Target, "Id"));
        if (extraWhere is not null)
        {
            where = new BinaryExpression(where, BinaryOperator.And, extraWhere);
        }

        var subquery = new SelectStatement(null,
            new QuerySpecification(
                false,
                [new ColumnSelectItem(projection, null)],
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Entitlements", true) }), new SqlIdentifier("e", true)),
                where, null, null),
            null, null);
        return PolicyPredicate.Create(new ExistsExpression(subquery), new Dictionary<string, PolicyValue>());
    }

    private CompileRequest Request() => new()
    {
        TargetDialect = TargetSqlDialect.SqlServer,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = _masks,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
        }
    };

    [Fact]
    public void ConstantOneProjection_OfAnExistsPolicy_IsAccepted_AndEmittedAsABoundValue()
    {
        _rowFilters.Predicates[Orders] = Exists(new LiteralExpression(1L, LiteralType.Integer));

        var compiled = _engine.Compile("SELECT id FROM orders".AsMemory(), Request(), CancellationToken.None);

        Assert.Contains("EXISTS (SELECT", compiled.Sql);
        Assert.DoesNotContain("SELECT 1 ", compiled.Sql);   // never inlined: bound like every other value
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.QueryLiteral && Equals(Convert.ToInt64(p.Value), 1L));
    }

    [Theory]
    [InlineData(2L)]
    [InlineData(0L)]
    public void AnyOtherIntegerProjection_StillFailsTheCoverageCheck(long value)
    {
        _rowFilters.Predicates[Orders] = Exists(new LiteralExpression(value, LiteralType.Integer));

        Assert.Throws<SecurityCoverageException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), Request(), CancellationToken.None));
    }

    [Fact]
    public void AStringProjection_StillFailsTheCoverageCheck()
    {
        _rowFilters.Predicates[Orders] = Exists(new LiteralExpression("x", LiteralType.String));

        Assert.Throws<SecurityCoverageException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), Request(), CancellationToken.None));
    }

    [Fact]
    public void ARawLiteralInTheWhereOfTheSubquery_StillFailsTheCoverageCheck()
    {
        // the exemption is the projection only: a value in a condition is a raw literal and is rejected
        _rowFilters.Predicates[Orders] = Exists(
            new LiteralExpression(1L, LiteralType.Integer),
            new BinaryExpression(Col("e", "Id"), BinaryOperator.Equal, new LiteralExpression(7L, LiteralType.Integer)));

        Assert.Throws<SecurityCoverageException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), Request(), CancellationToken.None));
    }

    [Fact]
    public void ALiteralOneOutsideAnExistsProjection_StillFailsTheCoverageCheck()
    {
        // an IN subquery or a scalar subquery projecting 1 is not an EXISTS: the value would decide the result
        var inSub = new InSubqueryExpression(
            Col(RowFilterAliases.Target, "Id"),
            new SelectStatement(null,
                new QuerySpecification(
                    false,
                    [new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null)],
                    new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Entitlements", true) }), new SqlIdentifier("e", true)),
                    null, null, null),
                null, null),
            false);
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(inSub, new Dictionary<string, PolicyValue>());

        Assert.Throws<SecurityCoverageException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), Request(), CancellationToken.None));
    }

    [Fact]
    public void PolicySubquery_JoiningATableWithoutATenantColumn_Compiles_AndTheTableIsStillADerivedTable()
    {
        // The lookup table has no tenant column (the catalog says so), so it has no tenant predicate to carry. In a join it must still be
        // wrapped in a derived table: the coverage verifier accepts a physical table only as the source of one. (A bare table failed the
        // proof with "A physical table is referenced outside a secured derived table", which denied every virtual filter and consent
        // filter that joins a table without a tenant column.)
        var from = new JoinedTableSource(
            new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Entitlements", true) }), new SqlIdentifier("e", true)),
            JoinType.Inner,
            new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Lookup", true) }), new SqlIdentifier("l", true)),
            new OnJoinCondition(new BinaryExpression(Col("l", "Code"), BinaryOperator.Equal, Col("e", "OrderId"))));
        var subquery = new SelectStatement(null,
            new QuerySpecification(
                false,
                [new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null)],
                from,
                new BinaryExpression(Col("e", "OrderId"), BinaryOperator.Equal, Col(RowFilterAliases.Target, "Id")), null, null),
            null, null);
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(new ExistsExpression(subquery), new Dictionary<string, PolicyValue>());

        var compiled = _engine.Compile("SELECT id FROM orders".AsMemory(), Request(), CancellationToken.None);

        Assert.Contains("[dbo].[Lookup]", compiled.Sql);
        Assert.Contains("(SELECT [Code] AS [Code] FROM [dbo].[Lookup]) AS [l]", compiled.Sql);   // wrapped, with the catalog projection, no predicate
    }
}
