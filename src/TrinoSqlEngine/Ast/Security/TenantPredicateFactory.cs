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
    public static Expression Build(DialectCapabilities capabilities, string tenantColumn, string parameterName, SqlParameterType parameterType, string? columnType = null, string? columnCollation = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentException.ThrowIfNullOrEmpty(tenantColumn);
        ArgumentException.ThrowIfNullOrEmpty(parameterName);

        Expression Column() => new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(tenantColumn, true) }));
        Expression Param() => new PolicyParameterExpression(parameterName, parameterType, ParameterOrigin.Tenant, ColumnType: columnType);

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
            case TenantComparisonStyle.EncodedBlob:
            {
                // col = t AND encode(CAST(col AS varchar)) = encode(CAST(t AS varchar)); BLOB comparison is byte-exact.
                static Expression Encoded(Expression operand) =>
                    new FunctionCallExpression(new SqlQualifiedName("encode"), new[] { (Expression)new CastExpression(operand, "varchar") });

                return new BinaryExpression(
                    new BinaryExpression(Column(), BinaryOperator.Equal, Param()),
                    BinaryOperator.And,
                    new BinaryExpression(Encoded(Column()), BinaryOperator.Equal, Encoded(Param())));
            }
            case TenantComparisonStyle.TextSendBytea:
            {
                // col = t AND textsend(CAST(col AS text)) = textsend(CAST(t AS text)); bytea comparison is byte-exact.
                static Expression Raw(Expression operand) =>
                    new FunctionCallExpression(new SqlQualifiedName("textsend"), new[] { (Expression)new CastExpression(operand, "text") });

                return new BinaryExpression(
                    new BinaryExpression(Column(), BinaryOperator.Equal, Param()),
                    BinaryOperator.And,
                    new BinaryExpression(Raw(Column()), BinaryOperator.Equal, Raw(Param())));
            }
            case TenantComparisonStyle.RawCast:
            {
                static Expression Raw(Expression operand) =>
                    new FunctionCallExpression(new SqlQualifiedName("UTL_RAW", "CAST_TO_RAW"), new[] { (Expression)new CastExpression(operand, "varchar") });

                return new BinaryExpression(
                    new BinaryExpression(Column(), BinaryOperator.Equal, Param()),
                    BinaryOperator.And,
                    new BinaryExpression(Raw(Column()), BinaryOperator.Equal, Raw(Param())));
            }
            case TenantComparisonStyle.CastBinary:
            {
                // CR-ADG-10: on a column whose catalog collation is UTF8_BINARY the plain equality is already byte-exact and sargable
                // (data skipping, partition pruning). Any other or unknown collation keeps the binary comparison.
                if (string.Equals(columnCollation, "UTF8_BINARY", StringComparison.OrdinalIgnoreCase))
                {
                    return new BinaryExpression(Column(), BinaryOperator.Equal, Param());
                }

                // Only the binary comparison. A plain "col = t" conjunct must NOT be added for the index/data-skipping benefit:
                // on a UTF8_LCASE column Spark's constant propagation turns "col = 'acme' AND CAST(col AS BINARY) = CAST('acme' AS BINARY)"
                // into a tautology (found by the Spark proxy test), which would make the comparison case-insensitive again.
                static Expression Raw(Expression operand) => new CastExpression(operand, "varbinary");

                return new BinaryExpression(Raw(Column()), BinaryOperator.Equal, Raw(Param()));
            }
            default:
                throw new NotSupportedException($"No binary-exact tenant comparison is defined for {capabilities.Dialect}.");
        }
    }
}
