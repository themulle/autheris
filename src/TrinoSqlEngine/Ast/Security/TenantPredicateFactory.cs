namespace TrinoSqlEngine.Ast.Security;

using System;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Builds the tenant predicate with the dialect's binary-exact comparison (SEC-ADG-04, INV-15, decision B-1). Tenant equality
/// never depends on the column collation or session settings: tenants <c>acme</c> and <c>ACME</c> are different tenants even
/// when the column is case-insensitive.
/// </summary>
public static class TenantPredicateFactory
{
    public static Expression Build(DialectCapabilities capabilities, string tenantColumn, string parameterName, SqlParameterType parameterType)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentException.ThrowIfNullOrEmpty(tenantColumn);
        ArgumentException.ThrowIfNullOrEmpty(parameterName);

        Expression Column() => new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(tenantColumn, true) }));
        Expression Param() => new PolicyParameterExpression(parameterName, parameterType, ParameterOrigin.Tenant);

        switch (capabilities.TenantComparison)
        {
            case TenantComparisonStyle.Utf16BinaryCast:
            {
                // col = @t AND CAST(CAST(col AS nvarchar(max)) AS varbinary(max)) = CAST(CAST(@t AS nvarchar(max)) AS varbinary(max))
                //   AND DATALENGTH(CAST(col AS nvarchar(max))) = DATALENGTH(CAST(@t AS nvarchar(max)))
                // The plain equality keeps the index seek; the binary conjunct makes the comparison exact.
                static Expression Binary(Expression operand) =>
                    new CastExpression(new CastExpression(operand, "varchar"), "varbinary");

                // SQL Server compares varbinary operands as if the shorter one were padded with zero bytes, so 'acme' and
                // 'acme' + NUL would be equal. The DATALENGTH conjunct closes that gap (tenant equality is exact, INV-15).
                static Expression Length(Expression operand) =>
                    new FunctionCallExpression(new SqlQualifiedName("DATALENGTH"), new[] { (Expression)new CastExpression(operand, "varchar") });

                return new BinaryExpression(
                    new BinaryExpression(
                        new BinaryExpression(Column(), BinaryOperator.Equal, Param()),
                        BinaryOperator.And,
                        new BinaryExpression(Binary(Column()), BinaryOperator.Equal, Binary(Param()))),
                    BinaryOperator.And,
                    new BinaryExpression(Length(Column()), BinaryOperator.Equal, Length(Param())));
            }
            default:
                throw new NotSupportedException($"No binary-exact tenant comparison is defined for {capabilities.Dialect}.");
        }
    }
}
