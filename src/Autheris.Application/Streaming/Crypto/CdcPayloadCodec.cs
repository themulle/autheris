namespace Autheris.Application.Streaming.Crypto;

using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;

/// <summary>A payload could not be encoded or decoded. Carries a stable code only, never row data.</summary>
public sealed class CdcPayloadCodecException : Exception
{
    public CdcPayloadCodecException()
        : this(CdcActivationCodes.UnsupportedType)
    {
    }

    public CdcPayloadCodecException(string code)
        : base(code)
    {
        Code = code;
    }

    public CdcPayloadCodecException(string code, Exception? innerException)
        : base(code, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Type-tagged JSON codec for stream payloads (PLAN-CDC-99 D-A7, SEC-CDC99-16). The tag set is closed: decoding never instantiates a type
/// named by the data, never uses polymorphic deserialisation, and is bounded in size and depth. Decoded dictionaries are immutable
/// (SEC-CDC99-05).
/// </summary>
public sealed class CdcPayloadCodec : ICdcPayloadCodec
{
    public static readonly CdcPayloadCodec Instance = new();

    public byte[] SerializeChange(CdcChangePayload payload)
    {
        try
        {
            return EncodeChange(payload);
        }
        catch (CdcPayloadCodecException ex) when (ex.Code == CdcActivationCodes.UnsupportedType)
        {
            throw new NotSupportedException("Unsupported type in change payload.", ex);
        }
    }

    public byte[] SerializeControl(CdcControlPayload payload) => EncodeControl(payload);

    public byte[] SerializeImage(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        try
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buffer))
            {
                WriteRow(w, row);
            }
            return buffer.WrittenSpan.ToArray();
        }
        catch (CdcPayloadCodecException ex) when (ex.Code == CdcActivationCodes.UnsupportedType)
        {
            throw new NotSupportedException("Unsupported type in row image.", ex);
        }
    }

    public IReadOnlyDictionary<string, object?> DeserializeImage(ReadOnlySpan<byte> plaintext)
    {
        try
        {
            using var doc = Parse(plaintext);
            return ReadRow(doc.RootElement) ?? FrozenDictionary<string, object?>.Empty;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            throw new CdcPayloadCodecException(InvalidCode, ex);
        }
    }

    public const int MaxDecodeBytes = 16 * 1024 * 1024;
    public const string InvalidCode = "CDC_PAYLOAD_INVALID";
    private const int MaxDepth = 8;

    public static byte[] EncodeChange(CdcChangePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WritePropertyName("k");
            WriteRow(w, payload.Key);
            w.WritePropertyName("b");
            WriteRow(w, payload.Before);
            w.WritePropertyName("a");
            WriteRow(w, payload.After);
            w.WriteBoolean("bu", payload.BeforeUnknown);
            w.WriteBoolean("po", payload.PayloadOversize);
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static CdcChangePayload DecodeChange(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var doc = Parse(utf8);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid();
            }

            var key = ReadRow(Required(root, "k")) ?? throw Invalid();
            var before = ReadRow(Required(root, "b"));
            var after = ReadRow(Required(root, "a"));
            return new CdcChangePayload(key, before, after, RequiredBool(root, "bu"), RequiredBool(root, "po"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            throw new CdcPayloadCodecException(InvalidCode, ex);
        }
    }

    public static byte[] EncodeControl(CdcControlPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("r", payload.Reason);
            WriteNullableLong(w, "sv", payload.StoredVersion);
            WriteNullableLong(w, "mv", payload.MinValidVersion);
            WriteNullableLong(w, "cv", payload.CurrentVersion);
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static CdcControlPayload DecodeControl(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var doc = Parse(utf8);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid();
            }

            return new CdcControlPayload(
                Required(root, "r").GetString() ?? throw Invalid(),
                ReadNullableLong(root, "sv"),
                ReadNullableLong(root, "mv"),
                ReadNullableLong(root, "cv"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            throw new CdcPayloadCodecException(InvalidCode, ex);
        }
    }

    /// <summary>Canonical, length-prefixed, type-tagged rendering of key values (no ambiguity between <c>("a|b")</c> and <c>("a","b")</c>).</summary>
    public static byte[] CanonicalKey(IReadOnlyList<object?> keyValues)
    {
        ArgumentNullException.ThrowIfNull(keyValues);
        var buffer = new ArrayBufferWriter<byte>(64);
        foreach (var value in keyValues)
        {
            var (tag, text) = Render(value);
            var tagBytes = System.Text.Encoding.UTF8.GetBytes(tag);
            var textBytes = System.Text.Encoding.UTF8.GetBytes(text);
            WriteInt(buffer, tagBytes.Length);
            buffer.Write(tagBytes);
            WriteInt(buffer, textBytes.Length);
            buffer.Write(textBytes);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteInt(ArrayBufferWriter<byte> buffer, int value)
    {
        var span = buffer.GetSpan(4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(span, value);
        buffer.Advance(4);
    }

    private static JsonDocument Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxDecodeBytes)
        {
            throw new CdcPayloadCodecException(InvalidCode);
        }

        return JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = MaxDepth });
    }

    private static CdcPayloadCodecException Invalid() => new(InvalidCode);

    private static JsonElement Required(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) ? v : throw Invalid();

    private static bool RequiredBool(JsonElement obj, string name)
    {
        var v = Required(obj, name);
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid()
        };
    }

    private static void WriteNullableLong(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static long? ReadNullableLong(JsonElement obj, string name)
    {
        var v = Required(obj, name);
        return v.ValueKind == JsonValueKind.Null ? null : v.GetInt64();
    }

    private static void WriteRow(Utf8JsonWriter w, IReadOnlyDictionary<string, object?>? row)
    {
        if (row is null)
        {
            w.WriteNullValue();
            return;
        }

        w.WriteStartObject();
        foreach (var (name, value) in row)
        {
            w.WritePropertyName(name);
            var (tag, text) = Render(value);
            w.WriteStartObject();
            w.WriteString("t", tag);
            if (text is not null && tag != "null")
            {
                w.WriteString("v", text);
            }

            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static IReadOnlyDictionary<string, object?>? ReadRow(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in el.EnumerateObject())
        {
            dict[prop.Name] = ReadValue(prop.Value);
        }

        return dict.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static (string Tag, string Text) Render(object? value) => value switch
    {
        null or DBNull => ("null", string.Empty),
        string s => ("s", s),
        long l => ("i64", l.ToString(CultureInfo.InvariantCulture)),
        int i => ("i32", i.ToString(CultureInfo.InvariantCulture)),
        short sh => ("i16", sh.ToString(CultureInfo.InvariantCulture)),
        byte b => ("u8", b.ToString(CultureInfo.InvariantCulture)),
        decimal d => ("dec", d.ToString(CultureInfo.InvariantCulture)),
        double dbl => ("f64", dbl.ToString("R", CultureInfo.InvariantCulture)),
        float f => ("f32", f.ToString("R", CultureInfo.InvariantCulture)),
        bool bo => ("bool", bo ? "true" : "false"),
        DateTime dt => ("dt", dt.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => ("dto", dto.ToString("O", CultureInfo.InvariantCulture)),
        DateOnly date => ("date", date.ToString("O", CultureInfo.InvariantCulture)),
        TimeOnly time => ("time", time.ToString("O", CultureInfo.InvariantCulture)),
        Guid g => ("guid", g.ToString("D")),
        byte[] bin => ("bin", Convert.ToBase64String(bin)),
        _ => throw new CdcPayloadCodecException(CdcActivationCodes.UnsupportedType)
    };

    private static object? ReadValue(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }

        var tag = Required(el, "t").GetString();
        if (tag == "null")
        {
            return null;
        }

        var text = Required(el, "v").GetString() ?? throw Invalid();
        var inv = CultureInfo.InvariantCulture;
        return tag switch
        {
            "s" => text,
            "i64" => long.Parse(text, inv),
            "i32" => int.Parse(text, inv),
            "i16" => short.Parse(text, inv),
            "u8" => byte.Parse(text, inv),
            "dec" => decimal.Parse(text, NumberStyles.Number, inv),
            "f64" => double.Parse(text, NumberStyles.Float, inv),
            "f32" => float.Parse(text, NumberStyles.Float, inv),
            "bool" => text switch { "true" => true, "false" => false, _ => throw Invalid() },
            "dt" => DateTime.Parse(text, inv, DateTimeStyles.RoundtripKind),
            "dto" => DateTimeOffset.Parse(text, inv, DateTimeStyles.RoundtripKind),
            "date" => DateOnly.ParseExact(text, "O", inv),
            "time" => TimeOnly.ParseExact(text, "O", inv),
            "guid" => Guid.ParseExact(text, "D"),
            "bin" => Convert.FromBase64String(text),
            _ => throw Invalid()
        };
    }
}
