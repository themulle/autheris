namespace Autheris.Application.Sql.ListParameters;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// Turns a raw list value (a JSON array of a WebSQL request, or a CLR collection produced by the declared-endpoint layer) into a
/// <see cref="ClientListValue"/> (REQ-SQL-128, PLAN-SQL-128 A-4). Throws <see cref="ClientListException"/> only: its message is the fixed
/// message of the code, so neither an element nor a count can reach a response, a log or an audit record (SEC-128-07).
/// Order of checks: raw length ceiling before any element is read (SEC-128-09), element rules, de-duplication (first occurrence kept),
/// distinct count against the configured maximum.
/// </summary>
public static class ListParameterNormalizer
{
    public static bool IsListCandidate(object? raw) => raw switch
    {
        null => false,
        JsonElement element => element.ValueKind == JsonValueKind.Array,
        string => false,
        byte[] => false,
        IDictionary => false,
        IEnumerable => true,
        _ => false
    };

    /// <param name="declaredElementType">The element type a declared query parameter requires; <c>null</c> infers it (one class, no mixing).</param>
    /// <param name="guidStrings">With <see cref="SqlParameterType.String"/>: every element must be a GUID and is bound in canonical "D" form.</param>
    public static ClientListValue Normalize(object raw, ListParameterLimits limits, SqlParameterType? declaredElementType, bool guidStrings = false)
    {
        ArgumentNullException.ThrowIfNull(raw);
        try
        {
            var items = Read(raw, limits.RawCeiling);
            return declaredElementType is { } declared
                ? BuildDeclared(items, declared, guidStrings, limits)
                : BuildInferred(items, limits);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException or ArgumentException and not ClientListException)
        {
            // JsonElement accessors and parsers: never a 500 and never the inner message (SEC-128-08).
            throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
        }
    }

    // ---- reading ---------------------------------------------------------------------------------------------------------------

    private static List<object?> Read(object raw, int ceiling)
    {
        if (raw is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            if (array.GetArrayLength() > ceiling)
            {
                throw new ClientListException(GovernedSqlErrorCodes.ListParamTooLarge);
            }

            var list = new List<object?>(array.GetArrayLength());
            foreach (var element in array.EnumerateArray())
            {
                list.Add(element);
            }

            return list;
        }

        if (raw is IEnumerable enumerable and not string and not byte[] and not IDictionary)
        {
            if (enumerable is ICollection collection && collection.Count > ceiling)
            {
                throw new ClientListException(GovernedSqlErrorCodes.ListParamTooLarge);
            }

            var list = new List<object?>();
            foreach (object? item in enumerable)
            {
                if (list.Count >= ceiling)
                {
                    throw new ClientListException(GovernedSqlErrorCodes.ListParamTooLarge);
                }

                list.Add(item);
            }

            return list;
        }

        throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
    }

    private static bool IsNull(object? item) => item is null || item is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    // ---- inferred type -----------------------------------------------------------------------------------------------------------

    private enum Kind { None, String, Number, Boolean }

    private static ClientListValue BuildInferred(List<object?> items, ListParameterLimits limits)
    {
        var values = new List<object>(items.Count);
        var kind = Kind.None;
        bool anyDecimal = false;
        bool anyDouble = false;
        bool anyLong = false;
        foreach (object? item in items)
        {
            if (IsNull(item))
            {
                throw new ClientListException(GovernedSqlErrorCodes.ListParamNull);
            }

            var (itemKind, value) = Classify(item!);
            if (kind != Kind.None && kind != itemKind)
            {
                throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
            }

            kind = itemKind;
            anyLong |= value is long;
            anyDecimal |= value is decimal;
            anyDouble |= value is double;
            values.Add(value);
        }

        if (anyDouble && (anyDecimal || anyLong))
        {
            // A double is never mixed with exact numbers: widening a long or decimal through a double could change an identifier.
            throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
        }

        var type = kind switch
        {
            Kind.Boolean => SqlParameterType.Boolean,
            Kind.Number when anyDouble => SqlParameterType.Double,
            Kind.Number when anyDecimal => SqlParameterType.Decimal,
            Kind.Number => SqlParameterType.Int64,
            _ => SqlParameterType.String
        };

        if (type == SqlParameterType.Decimal)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is long l)
                {
                    values[i] = (decimal)l;
                }
            }
        }

        return Finish(values, type, limits);
    }

    private static (Kind Kind, object Value) Classify(object item)
    {
        switch (item)
        {
            case JsonElement element:
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        return (Kind.String, element.GetString()!);
                    case JsonValueKind.True:
                        return (Kind.Boolean, true);
                    case JsonValueKind.False:
                        return (Kind.Boolean, false);
                    case JsonValueKind.Number:
                        return (Kind.Number, Number(element));
                    default:
                        throw new ClientListException(GovernedSqlErrorCodes.ListParamType);   // nested array or object
                }

            case string s:
                return (Kind.String, s);
            case bool b:
                return (Kind.Boolean, b);
            case long or int or short or byte or sbyte or ushort or uint:
                return (Kind.Number, Convert.ToInt64(item, CultureInfo.InvariantCulture));
            case decimal d:
                return (Kind.Number, d);
            case double or float:
                double value = Convert.ToDouble(item, CultureInfo.InvariantCulture);
                return double.IsFinite(value) ? (Kind.Number, value) : throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
            default:
                throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
        }
    }

    /// <summary>Int64 if it fits, else an exact decimal; a double only for a finite token with a fraction or exponent beyond decimal (SEC-128-08).</summary>
    private static object Number(JsonElement element)
    {
        if (element.TryGetInt64(out long integer))
        {
            return integer;
        }

        if (element.TryGetDecimal(out decimal number))
        {
            return number;
        }

        string text = element.GetRawText();
        bool exactTokenOnly = text.AsSpan().IndexOfAny('.', 'e', 'E') < 0;
        if (!exactTokenOnly && element.TryGetDouble(out double approximate) && double.IsFinite(approximate))
        {
            return approximate;
        }

        throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
    }

    // ---- declared type -----------------------------------------------------------------------------------------------------------

    private static ClientListValue BuildDeclared(List<object?> items, SqlParameterType declared, bool guidStrings, ListParameterLimits limits)
    {
        if (ClientListValue.ClrTypeOf(declared) is null)
        {
            throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
        }

        var values = new List<object>(items.Count);
        foreach (object? item in items)
        {
            if (IsNull(item))
            {
                throw new ClientListException(GovernedSqlErrorCodes.ListParamNull);
            }

            values.Add(Coerce(item!, declared, guidStrings));
        }

        return Finish(values, declared, limits);
    }

    private static object Coerce(object item, SqlParameterType declared, bool guidStrings)
    {
        // A JSON scalar is reduced to the CLR value it carries; a nested array or object is refused.
        string? text = null;
        long? integer = null;
        decimal? number = null;
        double? floating = null;
        bool? flag = null;
        switch (item)
        {
            case JsonElement element:
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        text = element.GetString();
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        flag = element.GetBoolean();
                        break;
                    case JsonValueKind.Number:
                        if (element.TryGetInt64(out long l)) integer = l;
                        if (element.TryGetDecimal(out decimal d)) number = d;
                        if (element.TryGetDouble(out double f) && double.IsFinite(f)) floating = f;
                        break;
                    default:
                        throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
                }

                break;
            case string s:
                text = s;
                break;
            case bool b:
                flag = b;
                break;
            case long or int or short or byte or sbyte or ushort or uint:
                integer = Convert.ToInt64(item, CultureInfo.InvariantCulture);
                number = integer;
                floating = integer;
                break;
            case decimal dec:
                number = dec;
                break;
            case double or float:
                double value = Convert.ToDouble(item, CultureInfo.InvariantCulture);
                if (double.IsFinite(value)) floating = value;
                break;
            case Guid guid:
                text = guid.ToString("D", CultureInfo.InvariantCulture);
                guidStrings = true;
                break;
            case DateTimeOffset offset:
                return declared == SqlParameterType.TimestampTz ? offset : throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
            case DateTime dateTime:
                return declared == SqlParameterType.TimestampTz
                    ? new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc))
                    : throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
            default:
                throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
        }

        switch (declared)
        {
            case SqlParameterType.String:
                if (text is null)
                {
                    throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
                }

                return guidStrings
                    ? Guid.TryParse(text, out var parsed) ? parsed.ToString("D", CultureInfo.InvariantCulture) : throw new ClientListException(GovernedSqlErrorCodes.ListParamType)
                    : text;
            case SqlParameterType.Int32:
            {
                if (integer is { } i && i is >= int.MinValue and <= int.MaxValue) return (int)i;
                if (text is not null && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsedInt)) return parsedInt;
                break;
            }

            case SqlParameterType.Int64:
            {
                if (integer is { } i) return i;
                if (text is not null && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsedLong)) return parsedLong;
                break;
            }

            case SqlParameterType.Decimal:
            {
                if (number is { } n) return n;
                if (text is not null && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out decimal parsedDecimal)) return parsedDecimal;
                break;
            }

            case SqlParameterType.Double:
            {
                if (floating is { } f) return f;
                if (text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedDouble) && double.IsFinite(parsedDouble)) return parsedDouble;
                break;
            }

            case SqlParameterType.Boolean:
            {
                if (flag is { } b) return b;
                if (text is not null && bool.TryParse(text, out bool parsedBool)) return parsedBool;
                break;
            }

            case SqlParameterType.TimestampTz:
            {
                // AssumeUniversal: an offset-less date never depends on the host time zone (SEC-128-08; #130 owns the policy).
                if (text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate)) return parsedDate;
                break;
            }

            case SqlParameterType.Timestamp:
            {
                if (text is not null && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedStamp)) return parsedStamp;
                break;
            }

            case SqlParameterType.Date:
            {
                if (text is not null && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDay)) return parsedDay;
                break;
            }
        }

        throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
    }

    // ---- result ------------------------------------------------------------------------------------------------------------------

    /// <summary>De-duplicates (exact equality, first occurrence kept) and checks the distinct count against the configured maximum.</summary>
    private static ClientListValue Finish(List<object> values, SqlParameterType type, ListParameterLimits limits)
    {
        var seen = new HashSet<object>(values.Count);
        var distinct = new List<object>(values.Count);
        foreach (object value in values)
        {
            if (seen.Add(value))
            {
                distinct.Add(value);
            }
        }

        if (distinct.Count > limits.ConfiguredMaxValues)
        {
            throw new ClientListException(GovernedSqlErrorCodes.ListParamTooLarge);
        }

        return ClientListValue.Create(distinct, type);
    }
}
