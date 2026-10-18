using System.Globalization;
using Autheris.Application.Analytics;
using Autheris.Domain.Exceptions;

namespace Autheris.Application.Sql.Tree;

/// <summary>
/// Coerces a raw filter value to the CLR type of a column kind (invariant culture, no locale surprises). Failures carry fixed texts
/// that never echo the client value (SEC-112-11).
/// </summary>
public static class FilterValueCoercion
{
    private const NumberStyles StrictDecimalStyles =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    public const string InvalidValueMessage = "The filter value is not valid for the column type.";
    public const string InvalidIsNullMessage = "The 'isNull' filter value must be a boolean (true or false).";

    public static object? Coerce(object? rawVal, ColumnValueKind kind, TreeFilterOperator op)
    {
        if (rawVal == null)
        {
            return null;
        }

        if (op == TreeFilterOperator.IsNull)
        {
            if (rawVal is bool b)
            {
                return b;
            }
            if (bool.TryParse(Convert.ToString(rawVal, CultureInfo.InvariantCulture), out var pb))
            {
                return pb;
            }
            throw new GatewayInvalidQueryException(InvalidIsNullMessage);
        }

        if (op is TreeFilterOperator.In or TreeFilterOperator.NotIn)
        {
            if (rawVal is IEnumerable<object?> list)
            {
                return list.Select(item => CoerceSingle(item, kind)).ToList();
            }
            return new List<object?> { CoerceSingle(rawVal, kind) };
        }

        return CoerceSingle(rawVal, kind);
    }

    public static object? CoerceSingle(object? val, ColumnValueKind kind)
    {
        if (val == null)
        {
            return null;
        }

        switch (kind)
        {
            case ColumnValueKind.Int:
                if (val is int iVal) return iVal;
                if (val is long lVal)
                {
                    if (lVal is < int.MinValue or > int.MaxValue)
                    {
                        throw new GatewayInvalidQueryException(InvalidValueMessage);
                    }
                    return (int)lVal;
                }
                if (val is short sVal) return (int)sVal;
                if (val is byte bVal) return (int)bVal;
                break;

            case ColumnValueKind.Long:
                if (val is long lVal2) return lVal2;
                if (val is int iVal2) return (long)iVal2;
                if (val is short sVal2) return (long)sVal2;
                if (val is byte bVal2) return (long)bVal2;
                break;

            case ColumnValueKind.Decimal:
                if (val is decimal decVal) return decVal;
                if (val is int iDec) return (decimal)iDec;
                if (val is long lDec) return (decimal)lDec;
                if (val is double dDec) return (decimal)dDec;
                if (val is float fDec) return (decimal)fDec;
                break;

            case ColumnValueKind.Float:
                if (val is double dVal) return dVal;
                if (val is float fVal) return (double)fVal;
                if (val is decimal decF) return (double)decF;
                if (val is int iF) return (double)iF;
                if (val is long lF) return (double)lF;
                break;

            case ColumnValueKind.Boolean:
                if (val is bool bVal3) return bVal3;
                break;

            case ColumnValueKind.DateTime:
                if (val is DateTimeOffset dtoVal) return dtoVal;
                // R-GQL-10: a DateTime without kind is UTC, not server local time.
                if (val is DateTime dtVal)
                {
                    return new DateTimeOffset(
                        dtVal.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dtVal, DateTimeKind.Utc) : dtVal.ToUniversalTime(),
                        TimeSpan.Zero);
                }
                break;

            default:
                if (val is string sVal3) return sVal3;
                break;
        }

        var str = Convert.ToString(val, CultureInfo.InvariantCulture) ?? string.Empty;

        return kind switch
        {
            ColumnValueKind.Int => int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                ? (object)i
                : throw new GatewayInvalidQueryException(InvalidValueMessage),
            ColumnValueKind.Long => long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                ? (object)l
                : throw new GatewayInvalidQueryException(InvalidValueMessage),
            ColumnValueKind.Decimal => decimal.TryParse(str, StrictDecimalStyles, CultureInfo.InvariantCulture, out var d)
                ? (object)d
                : throw new GatewayInvalidQueryException(InvalidValueMessage),
            ColumnValueKind.Float => double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                ? (object)f
                : throw new GatewayInvalidQueryException(InvalidValueMessage),
            ColumnValueKind.Boolean => bool.TryParse(str, out var pb)
                ? (object)pb
                : throw new GatewayInvalidQueryException(InvalidValueMessage),
            ColumnValueKind.DateTime => DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
                ? (object)dto
                : throw new GatewayInvalidQueryException(InvalidValueMessage),
            _ => str
        };
    }
}
