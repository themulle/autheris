namespace Autheris.Application.DataSources.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Implementation of deterministic v2 keyset cursor encoder and decoder.
/// Generates opaque signed <c>v2|kid|iat|payload.sig</c> tokens over composite primary key values and request binding context.
/// Verifies HMAC-SHA256 signature in constant time before inspecting fields, rejects non-v2 cursors, unknown key ids,
/// cursors older than 24 hours, and future-dated timestamps beyond 5 minutes of clock skew, and returns one uniform error.
/// </summary>
public sealed class KeysetCursorEncoder : IKeysetCursorEncoder
{
    private const string CursorVersion = "v2";
    private static readonly byte[] KeyIdDomain = Encoding.UTF8.GetBytes("autheris:keyset-cursor:kid:v2");
    private static readonly TimeSpan MaxCursorAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    private readonly byte[] _signingKeyBytes;
    private readonly byte[] _keyIdBytes;
    private readonly string _keyId;
    private readonly TimeProvider _clock;
    private readonly ILogger<KeysetCursorEncoder>? _logger;

    [ActivatorUtilitiesConstructor]
    public KeysetCursorEncoder(
        IOptions<GatewayOptions> gatewayOptions,
        IHostEnvironment? environment = null,
        TimeProvider? clock = null,
        ILogger<KeysetCursorEncoder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(gatewayOptions);
        _clock = clock ?? TimeProvider.System;
        _logger = logger;

        var rawKey = gatewayOptions.Value?.RestApi?.BulkStream?.SigningKey;
        if (environment?.IsDevelopment() == true)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                _signingKeyBytes = RandomNumberGenerator.GetBytes(SecretKeyRequirements.MinimumKeyBytes);
                _logger?.LogWarning(
                    "Gateway:RestApi:BulkStream:SigningKey is not configured; using an instance-local ephemeral key in Development. " +
                    "Keyset cursors are only valid for this instance until restart.");
            }
            else
            {
                _signingKeyBytes = Encoding.UTF8.GetBytes(rawKey.Trim());
            }
        }
        else
        {
            if (!SecretKeyRequirements.TryValidateCursorSigningKey(rawKey, out var validatedKey, out var errorMessage))
            {
                throw new InvalidOperationException(errorMessage);
            }

            _signingKeyBytes = validatedKey;
        }

        _keyId = DeriveKeyId(_signingKeyBytes);
        _keyIdBytes = Encoding.UTF8.GetBytes(_keyId);
    }

    public KeysetCursorEncoder(string signingKey, TimeProvider? clock = null)
    {
        if (!SecretKeyRequirements.TryValidateCursorSigningKey(signingKey, out var validatedKey, out var errorMessage))
        {
            throw new ArgumentException(errorMessage, nameof(signingKey));
        }

        _clock = clock ?? TimeProvider.System;
        _signingKeyBytes = validatedKey;
        _keyId = DeriveKeyId(_signingKeyBytes);
        _keyIdBytes = Encoding.UTF8.GetBytes(_keyId);
    }

    public KeysetCursorEncoder(byte[] signingKeyBytes, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(signingKeyBytes);
        if (signingKeyBytes.Length < SecretKeyRequirements.MinimumKeyBytes)
        {
            throw new ArgumentException(
                $"Cursor signing key must be at least {SecretKeyRequirements.MinimumKeyBytes} bytes long.",
                nameof(signingKeyBytes));
        }

        _clock = clock ?? TimeProvider.System;
        _signingKeyBytes = (byte[])signingKeyBytes.Clone();
        _keyId = DeriveKeyId(_signingKeyBytes);
        _keyIdBytes = Encoding.UTF8.GetBytes(_keyId);
    }

    public string EncodeCursor(IReadOnlyDictionary<string, object?> pkValues, KeysetCursorContext context)
    {
        ArgumentNullException.ThrowIfNull(pkValues);
        ArgumentNullException.ThrowIfNull(context);
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
        var payload = Base64UrlEncode(bytes);
        var issuedAtSeconds = _clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var envelope = $"{CursorVersion}|{_keyId}|{issuedAtSeconds}|{payload}";
        var sig = ComputeSignature(envelope, context);
        return $"{envelope}.{sig}";
    }

    public IReadOnlyDictionary<string, object?> DecodeCursor(string cursorBase64, TableMetadata metadata, KeysetCursorContext context)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(cursorBase64))
        {
            throw FailInvalidCursor("empty");
        }

        var dotIndex = cursorBase64.LastIndexOf('.');
        if (dotIndex <= 0 || dotIndex >= cursorBase64.Length - 1)
        {
            throw FailInvalidCursor("missing_signature");
        }

        var envelope = cursorBase64[..dotIndex];
        var providedSig = cursorBase64[(dotIndex + 1)..];

        // Verify the MAC first in constant time over the exact envelope and bound context before parsing any header or payload field.
        var expectedSig = ComputeSignature(envelope, context);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedSig), Encoding.UTF8.GetBytes(providedSig)))
        {
            throw FailInvalidCursor("mac_mismatch");
        }

        var parts = envelope.Split('|');
        if (parts.Length != 4)
        {
            throw FailInvalidCursor("envelope_format");
        }

        if (!string.Equals(parts[0], CursorVersion, StringComparison.Ordinal))
        {
            throw FailInvalidCursor("unsupported_version");
        }

        if (!CryptographicOperations.FixedTimeEquals(_keyIdBytes, Encoding.UTF8.GetBytes(parts[1])))
        {
            throw FailInvalidCursor("unknown_kid");
        }

        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var iatSeconds))
        {
            throw FailInvalidCursor("invalid_iat");
        }

        var nowSeconds = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (nowSeconds - iatSeconds > (long)MaxCursorAge.TotalSeconds)
        {
            throw FailInvalidCursor("expired");
        }

        if (iatSeconds - nowSeconds > (long)MaxFutureSkew.TotalSeconds)
        {
            throw FailInvalidCursor("future_iat");
        }

        if (!TryBase64UrlDecode(parts[3], out var bytes))
        {
            throw FailInvalidCursor("malformed_base64");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw FailInvalidCursor("malformed_json");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw FailInvalidCursor("non_object_root");
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
                    throw FailInvalidCursor("duplicate_property");
                }

                if (!canonicalPkMap.ContainsKey(prop.Name))
                {
                    throw FailInvalidCursor("extraneous_property");
                }
            }

            if (propMap.Count != canonicalPkMap.Count)
            {
                foreach (var expected in canonicalPkMap.Keys)
                {
                    if (!propMap.ContainsKey(expected))
                    {
                        throw FailInvalidCursor("missing_key_column");
                    }
                }
            }

            var result = new Dictionary<string, object?>(canonicalPkMap.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (canonicalName, _) in canonicalPkMap)
            {
                var jsonValue = propMap[canonicalName];
                if (jsonValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    throw FailInvalidCursor("null_key_value");
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

    private object ConvertJsonValueToColumnType(JsonElement element, TableColumn column)
    {
        var dataType = column.DataType?.Trim().ToLowerInvariant() ?? "varchar";
        try
        {
            if (dataType.Contains("int", StringComparison.Ordinal) || dataType.Contains("serial", StringComparison.Ordinal))
            {
                if (element.ValueKind != JsonValueKind.Number)
                {
                    throw FailInvalidCursor("type_mismatch");
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
                    throw FailInvalidCursor("type_mismatch");
                }
                return element.GetDecimal();
            }

            if (dataType.Contains("float", StringComparison.Ordinal) || dataType.Contains("double", StringComparison.Ordinal) || dataType.Contains("real", StringComparison.Ordinal))
            {
                if (element.ValueKind != JsonValueKind.Number)
                {
                    throw FailInvalidCursor("type_mismatch");
                }
                return element.GetDouble();
            }

            if (dataType.Contains("bool", StringComparison.Ordinal))
            {
                if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw FailInvalidCursor("type_mismatch");
                }
                return element.GetBoolean();
            }

            if (dataType.Contains("guid", StringComparison.Ordinal) || dataType.Contains("uuid", StringComparison.Ordinal))
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    throw FailInvalidCursor("type_mismatch");
                }
                var str = element.GetString();
                if (string.IsNullOrWhiteSpace(str) || !Guid.TryParse(str, out var guid))
                {
                    throw FailInvalidCursor("type_mismatch");
                }
                return guid;
            }

            if (dataType.Contains("date", StringComparison.Ordinal) || dataType.Contains("time", StringComparison.Ordinal))
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    throw FailInvalidCursor("type_mismatch");
                }
                var str = element.GetString();
                if (string.IsNullOrWhiteSpace(str) || !DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                {
                    throw FailInvalidCursor("type_mismatch");
                }
                return dto;
            }

            // Default to string
            if (element.ValueKind != JsonValueKind.String)
            {
                throw FailInvalidCursor("type_mismatch");
            }
            return element.GetString()!;
        }
        catch (InvalidCursorException)
        {
            throw;
        }
        catch (Exception)
        {
            throw FailInvalidCursor("value_conversion");
        }
    }

    private InvalidCursorException FailInvalidCursor(string reasonCode)
    {
        _logger?.LogDebug("Rejected keyset cursor ({ReasonCode}).", reasonCode);
        return new InvalidCursorException(InvalidCursorException.UniformMessage);
    }

    private static string DeriveKeyId(byte[] signingKeyBytes)
    {
        var hash = HMACSHA256.HashData(signingKeyBytes, KeyIdDomain);
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryBase64UrlDecode(string input, [NotNullWhen(true)] out byte[]? bytes)
    {
        var padded = input.Trim().Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1:
                bytes = null;
                return false;
        }

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = null;
            return false;
        }
    }

    /// <summary>
    /// Canonical, injective rendering of the context: every field is length-prefixed so a delimiter inside a value
    /// (for example a tenant id containing <c>|</c>) cannot shift content into a neighbouring field. Identifiers are
    /// case-insensitive in the catalog and are lower-cased; tenant, caller and request hash are compared exactly.
    /// </summary>
    private static string FormatContext(KeysetCursorContext context)
    {
        var sb = new StringBuilder("kc2");
        AppendField(sb, context.TenantId);
        AppendField(sb, context.DataSource?.ToLowerInvariant());
        AppendField(sb, context.Schema?.ToLowerInvariant());
        AppendField(sb, context.TableName?.ToLowerInvariant());
        AppendField(sb, context.Direction?.ToLowerInvariant());
        if (context.Columns is null)
        {
            sb.Append("|-");
        }
        else
        {
            sb.Append('|').Append(context.Columns.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var column in context.Columns)
            {
                AppendField(sb, column?.ToLowerInvariant());
            }
        }

        AppendField(sb, context.Caller);
        AppendField(sb, context.RequestHash);
        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string? value)
    {
        if (value is null)
        {
            sb.Append("|-");
            return;
        }

        sb.Append('|').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    private string ComputeSignature(string envelope, KeysetCursorContext context)
    {
        var dataToSign = $"{envelope.Length.ToString(CultureInfo.InvariantCulture)}:{envelope}|{FormatContext(context)}";
        var hash = HMACSHA256.HashData(_signingKeyBytes, Encoding.UTF8.GetBytes(dataToSign));
        return Base64UrlEncode(hash);
    }
}
