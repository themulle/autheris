namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autheris.Application.Data.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// PLAN-DATA-178 / SEC-DATA178-03: Implementation of deterministic keyset cursor encoder and decoder.
/// Generates opaque base64url JSON tuples of composite primary key values.
/// Validates cursor integrity and strong typing to prevent SQL injection or pagination desynchronization.
/// </summary>
public sealed class KeysetCursorEncoder : IKeysetCursorEncoder
{
    public string EncodeCursor(IReadOnlyDictionary<string, object?> pkValues)
    {
        ArgumentNullException.ThrowIfNull(pkValues);
        if (pkValues.Count == 0)
        {
            throw new ArgumentException("Primary key values cannot be empty.", nameof(pkValues));
        }

        using var memoryStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memoryStream))
        {
            writer.WriteStartObject();
            foreach (var kvp in pkValues)
            {
                writer.WritePropertyName(kvp.Key);
                WriteJsonValue(writer, kvp.Value);
            }
            writer.WriteEndObject();
        }

        var bytes = memoryStream.ToArray();
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public IReadOnlyDictionary<string, object?> DecodeCursor(string cursorBase64, TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(cursorBase64))
        {
            throw new InvalidCursorException("INVALID_CURSOR: Cursor cannot be empty.");
        }

        byte[] bytes;
        try
        {
            var padded = cursorBase64.Trim().Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
                case 1: throw new InvalidCursorException("INVALID_CURSOR: Malformed base64 length.");
            }
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException ex)
        {
            throw new InvalidCursorException("INVALID_CURSOR: Malformed base64 encoding.", ex);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new InvalidCursorException("INVALID_CURSOR: Malformed JSON cursor payload.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidCursorException("INVALID_CURSOR: Root must be a JSON object.");
            }

            IReadOnlyList<string> pkCols = metadata.PrimaryKeyColumns;
            if (pkCols.Count == 0)
            {
                var uniqueIdx = metadata.Indexes.FirstOrDefault(i => i.IsUnique && i.Columns.Count > 0);
                if (uniqueIdx != null)
                {
                    pkCols = uniqueIdx.Columns;
                }
                else
                {
                    throw new KeysetStreamingUnavailableException("KEYSET_STREAMING_UNAVAILABLE: Table requires a primary key or unique index.");
                }
            }

            var canonicalPkMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in pkCols)
            {
                var colMeta = metadata.GetColumn(col)
                    ?? throw new KeysetStreamingUnavailableException($"KEYSET_STREAMING_UNAVAILABLE: Key column '{col}' not found on table.");
                canonicalPkMap[colMeta.ColumnName] = colMeta.ColumnName;
            }

            var propMap = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!propMap.TryAdd(prop.Name, prop.Value))
                {
                    throw new InvalidCursorException($"INVALID_CURSOR: Duplicate property '{prop.Name}' in cursor.");
                }

                if (!canonicalPkMap.ContainsKey(prop.Name))
                {
                    throw new InvalidCursorException($"INVALID_CURSOR: Extraneous property '{prop.Name}' in cursor does not match primary key columns.");
                }
            }

            if (propMap.Count != canonicalPkMap.Count)
            {
                foreach (var expected in canonicalPkMap.Keys)
                {
                    if (!propMap.ContainsKey(expected))
                    {
                        throw new InvalidCursorException($"INVALID_CURSOR: Missing key column '{expected}' in cursor.");
                    }
                }
            }

            var result = new Dictionary<string, object?>(canonicalPkMap.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (canonicalName, _) in canonicalPkMap)
            {
                var jsonValue = propMap[canonicalName];
                if (jsonValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    throw new InvalidCursorException($"INVALID_CURSOR: Primary key value for '{canonicalName}' cannot be null.");
                }

                var colMeta = metadata.GetColumn(canonicalName)!;
                var typedValue = ConvertJsonValueToColumnType(jsonValue, colMeta);
                result[canonicalName] = typedValue;
            }

            return result;
        }
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? val)
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
                writer.WriteStringValue(dt.ToString("o", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("o", CultureInfo.InvariantCulture));
                break;
            case Guid g:
                writer.WriteStringValue(g.ToString());
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            default:
                writer.WriteStringValue(val.ToString());
                break;
        }
    }

    private static object ConvertJsonValueToColumnType(JsonElement element, TableColumn column)
    {
        var dataType = column.DataType?.Trim().ToLowerInvariant() ?? "varchar";
        try
        {
            if (dataType.Contains("int", StringComparison.Ordinal) || dataType.Contains("serial", StringComparison.Ordinal))
            {
                if (element.ValueKind != JsonValueKind.Number)
                {
                    throw new InvalidCursorException($"INVALID_CURSOR: Column '{column.ColumnName}' requires numeric integer value.");
                }

                if (dataType.Contains("bigint", StringComparison.Ordinal) || dataType.Contains("int8", StringComparison.Ordinal) || dataType.Contains("long", StringComparison.Ordinal))
                {
                    return element.GetInt64();
                }

                if (dataType.Contains("smallint", StringComparison.Ordinal) || dataType.Contains("int2", StringComparison.Ordinal))
                {
                    return element.GetInt16();
                }

                return element.GetInt32();
            }

            if (dataType.Contains("decimal", StringComparison.Ordinal) || dataType.Contains("numeric", StringComparison.Ordinal) || dataType.Contains("money", StringComparison.Ordinal))
            {
                if (element.ValueKind != JsonValueKind.Number)
                {
                    throw new InvalidCursorException($"INVALID_CURSOR: Column '{column.ColumnName}' requires numeric decimal value.");
                }
                return element.GetDecimal();
            }

            if (dataType.Contains("float", StringComparison.Ordinal) || dataType.Contains("double", StringComparison.Ordinal) || dataType.Contains("real", StringComparison.Ordinal))
            {
                if (element.ValueKind != JsonValueKind.Number)
                {
                    throw new InvalidCursorException($"INVALID_CURSOR: Column '{column.ColumnName}' requires floating point value.");
                }
                return element.GetDouble();
            }

            if (dataType.Contains("bool", StringComparison.Ordinal))
            {
                if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidCursorException($"INVALID_CURSOR: Column '{column.ColumnName}' requires boolean value.");
                }
                return element.GetBoolean();
            }

            if (dataType.Contains("guid", StringComparison.Ordinal) || dataType.Contains("uuid", StringComparison.Ordinal))
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidCursorException($"INVALID_CURSOR: Column '{column.ColumnName}' requires string GUID value.");
                }
                var str = element.GetString();
                if (string.IsNullOrWhiteSpace(str) || !Guid.TryParse(str, out var guid))
                {
                    throw new InvalidCursorException($"INVALID_CURSOR: Invalid GUID format for column '{column.ColumnName}'.");
                }
                return guid;
            }

            if (dataType.Contains("date", StringComparison.Ordinal) || dataType.Contains("time", StringComparison.Ordinal))
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidCursorException($"INVALID_CURSOR: Column '{column.ColumnName}' requires ISO-8601 date/time string.");
                }
                var str = element.GetString();
                if (string.IsNullOrWhiteSpace(str) || !DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                {
                    throw new InvalidCursorException($"INVALID_CURSOR: Invalid timestamp format for column '{column.ColumnName}'.");
                }
                return dto;
            }

            // Default to string
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new InvalidCursorException($"INVALID_CURSOR: Column '{column.ColumnName}' requires string value.");
            }
            return element.GetString()!;
        }
        catch (InvalidCursorException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidCursorException($"INVALID_CURSOR: Failed to convert value for column '{column.ColumnName}' to type '{column.DataType}'.", ex);
        }
    }
}
