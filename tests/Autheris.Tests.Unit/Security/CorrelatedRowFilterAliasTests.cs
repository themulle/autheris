using System.Collections.Generic;
using System.Linq;
using System.Security;
using Autheris.Tests.Unit.Sql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>
/// Correlated row filters (EXISTS ... WHERE dep.pk = target.fk) bind to the reserved target alias that the query
/// builders put on the filtered table.
/// </summary>
[Trait("Category", "Security")]
public sealed class CorrelatedRowFilterAliasTests
{
    private static ColumnReference Col(params string[] parts) =>
        new(new SqlQualifiedName(parts.Select(p => new SqlIdentifier(p, false)).ToList()));

    /// <summary>EXISTS (SELECT 1 FROM customers c WHERE c.id = autheris_target.customer_id): a correlated row policy.</summary>
    private static PolicyPredicate CorrelatedPolicy() => PolicyPredicate.Create(
        new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("customers", false) }), new SqlIdentifier("c", false)),
                new BinaryExpression(Col("c", "id"), BinaryOperator.Equal, Col(RowFilterAliases.Target, "customer_id")),
                null, null), null, null)),
        new Dictionary<string, PolicyValue>());

    private static PolicyPredicate AmountPolicy() => PolicyPredicate.Create(
        new BinaryExpression(Col("amount"), BinaryOperator.Equal, new PolicyParameterExpression("__pol_amount", SqlParameterType.Int64)),
        new Dictionary<string, PolicyValue> { ["__pol_amount"] = new(1L, SqlParameterType.Int64) });

    private static TestCompileEnv Env(PolicyPredicate policy) => new TestCompileEnv(TargetSqlDialect.PostgreSql)
        .Table("orders", "tenant_id", "id:INTEGER", "customer_id:INTEGER", "amount:INTEGER", "tenant_id")
        .Table("customers", "tenant_id", "id:INTEGER", "country", "tenant_id")
        .Policy("orders", policy);

    // The legacy RlsListener tests (RlsListener_RejectsCorrelatedFilterOnDml, ..._AliasesFilteredTableOnlyForCorrelatedFilters) are
    // ported to the typed compiler path: the same properties, asserted on Compile.

    [Theory]
    [InlineData("DELETE FROM orders WHERE id = 1")]
    [InlineData("UPDATE orders SET amount = 0 WHERE id = 1")]
    public void Compile_RejectsCorrelatedFilterOnDml(string dml)
    {
        var env = Env(CorrelatedPolicy());

        Should.Throw<SecurityException>(() => env.Compile(dml, statements: StatementPermissions.Update | StatementPermissions.Delete));
    }

    [Fact]
    public void Compile_AliasesFilteredTableOnlyForCorrelatedFilters()
    {
        Env(CorrelatedPolicy()).Compile("SELECT id FROM orders").Sql.ShouldContain(RowFilterAliases.Target);
        Env(AmountPolicy()).Compile("SELECT id FROM orders").Sql.ShouldNotContain(RowFilterAliases.Target);
    }
}
