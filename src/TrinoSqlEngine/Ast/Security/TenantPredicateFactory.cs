namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Security;
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
    /// <param name="columnCollation">Kept for callers; no dialect reads it (the collation is never trusted for the tenant comparison, SEC-X1-09).</param>
    public static Expression Build(DialectCapabilities capabilities, string tenantColumn, string parameterName, SqlParameterType parameterType, string? columnType = null, string? columnCollation = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentException.ThrowIfNullOrEmpty(tenantColumn);
        ArgumentException.ThrowIfNullOrEmpty(parameterName);

        if (capabilities.TenantComparison == TenantComparisonStyle.CastBlob && !SqliteTypeAffinity.IsText(columnType))
        {
            // SEC-X1-09: fail closed. A NUMERIC, INTEGER, REAL or BLOB affinity column no longer holds the text that was written.
            throw new SecurityException("The tenant column of a SQLite table must have TEXT affinity.");
        }

        Expression column = new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(tenantColumn, true) }));
        Expression param = new PolicyParameterExpression(parameterName, parameterType, ParameterOrigin.Tenant, ColumnType: columnType);
        return ExactEquals(capabilities, column, param, columnCollation);
    }

    /// <summary>
    /// The dialect's byte-exact equality of two string operands: <c>column = value</c> plus the binary comparison (decision B-1).
    /// CR-ADG-42 reuses it for the string equality of an INSERT check, so the check is never looser than any collation. The operands
    /// are used as given (an annotated policy parameter keeps its column type).
    /// </summary>
    public static Expression ExactEquals(DialectCapabilities capabilities, Expression column, Expression value, string? columnCollation = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        Expression Column() => column;
        Expression Param() => value;

#if DEBUG
        if (CompilerTestSeams.IsDisabled(SecurityControl.TenantByteExactCompare))
        {
            return new BinaryExpression(Column(), BinaryOperator.Equal, Param());
        }
#endif

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

                var binaryEquality = new BinaryExpression(
                    new BinaryExpression(Column(), BinaryOperator.Equal, Param()),
                    BinaryOperator.And,
                    new BinaryExpression(Binary(Column()), BinaryOperator.Equal, Binary(Param())));
#if DEBUG
                if (CompilerTestSeams.IsDisabled(SecurityControl.TenantBinaryLengthConjunct))
                {
                    return binaryEquality;
                }
#endif
                return new BinaryExpression(
                    binaryEquality,
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
                    new FunctionCallExpression(new SqlQualifiedName("SYS", "UTL_RAW", "CAST_TO_RAW"), new[] { (Expression)new CastExpression(operand, "varchar") });

                return new BinaryExpression(
                    new BinaryExpression(Column(), BinaryOperator.Equal, Param()),
                    BinaryOperator.And,
                    new BinaryExpression(Raw(Column()), BinaryOperator.Equal, Raw(Param())));
            }
            case TenantComparisonStyle.CastBinary:
            {
                // SEC-X1-09 (WP-X0j): the binary template is always used. The former plain-equality relaxation for a catalog collation
                // of UTF8_BINARY trusted cached metadata, so a collation change inside the cache TTL would have made the tenant comparison
                // case-insensitive. X1-DBX may re-enable it only with G9 evidence and a catalog freshness check per compile.
                // Only the binary comparison. A plain "col = t" conjunct must NOT be added for the index/data-skipping benefit:
                // on a UTF8_LCASE column Spark's constant propagation turns "col = 'acme' AND CAST(col AS BINARY) = CAST('acme' AS BINARY)"
                // into a tautology (found by the Spark proxy test), which would make the comparison case-insensitive again.
                static Expression Raw(Expression operand) => new CastExpression(operand, "varbinary");

#if DEBUG
                if (CompilerTestSeams.IsDisabled(SecurityControl.DatabricksUtf8BinaryRelaxation) && columnCollation == "UTF8_BINARY")
                {
                    return new BinaryExpression(Column(), BinaryOperator.Equal, Param());
                }
#endif
                return new BinaryExpression(Raw(Column()), BinaryOperator.Equal, Raw(Param()));
            }
            case TenantComparisonStyle.CastBlob:
            {
                // col = t AND CAST(col AS BLOB) = CAST(t AS BLOB); BLOB comparison is memcmp, whatever the column collation (NOCASE, RTRIM).
                static Expression Raw(Expression operand) => new CastExpression(operand, "blob");

                return new BinaryExpression(
                    new BinaryExpression(Column(), BinaryOperator.Equal, Param()),
                    BinaryOperator.And,
                    new BinaryExpression(Raw(Column()), BinaryOperator.Equal, Raw(Param())));
            }
            default:
                throw new NotSupportedException($"No binary-exact tenant comparison is defined for {capabilities.Dialect}.");
        }
    }
}
