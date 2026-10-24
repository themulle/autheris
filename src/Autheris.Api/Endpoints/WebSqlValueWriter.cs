namespace Autheris.Api.Endpoints;

using System;
using System.Globalization;
using System.Text.Json;

/// <summary>
/// Writes provider values as JSON for WebSQL result streams. All formatting is culture-invariant
/// with fixed ISO formats (SEC-PGP-07).
/// </summary>
internal static class WebSqlValueWriter
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "HH:mm:ss.FFFFFFF";

    internal static void Write(Utf8JsonWriter writer, object? val)
    {
        if (val is null or DBNull)
        {
            writer.WriteNullValue();
            return;
        }

        switch (val)
        {
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte by:
                writer.WriteNumberValue(by);
                break;
            case short s:
                writer.WriteNumberValue(s);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal dec:
                writer.WriteNumberValue(dec);
                break;
            case DateTime dt:
                writer.WriteStringValue(ToUtc(dt).ToString(TimestampFormat, CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
                break;
            case DateOnly date:
                writer.WriteStringValue(date.ToString(DateFormat, CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                writer.WriteStringValue(time.ToString(TimeFormat, CultureInfo.InvariantCulture));
                break;
            case TimeSpan ts:
                writer.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture));
                break;
            case Guid g:
                writer.WriteStringValue(g.ToString("D", CultureInfo.InvariantCulture));
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            case string str:
                writer.WriteStringValue(str);
                break;
            case IFormattable formattable:
                writer.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                WriteFallback(writer, val);
                break;
        }
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static void WriteFallback(Utf8JsonWriter writer, object val)
    {
        var text = Convert.ToString(val, CultureInfo.InvariantCulture);
        if (text is null || string.Equals(text, val.GetType().ToString(), StringComparison.Ordinal))
        {
            // Default Object.ToString() would leak the CLR type name.
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(text);
    }
}
