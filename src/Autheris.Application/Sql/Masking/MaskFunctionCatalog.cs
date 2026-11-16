namespace Autheris.Application.Sql.Masking;

using System;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Domain.Model;
using TrinoSqlEngine.Governance;

public enum ClassificationColumnFamily
{
    Text,
    Numeric,
    DateOrTimestamp,
    Boolean,
    BinaryOrOther
}

public sealed record ParsedMaskParameters(
    string? Replacement = null,
    int KeepFirst = 1,
    int KeepLast = 0,
    char MaskChar = '*',
    string? KeyRef = null,
    string TokenDomain = "DEFAULT",
    MaskDateTruncateUnit TruncateUnit = MaskDateTruncateUnit.Month,
    decimal? BucketWidth = null,
    int RoundDigits = 0,
    int GeoDecimals = 2);

/// <summary>
/// Central catalogue rules for the 11 <see cref="MaskFunction"/> values: strict JSON parameter parsing and
/// validation, column-type compatibility checks, and deterministic TYPE_SAFE_REDACT fallback detection.
/// </summary>
public static partial class MaskFunctionCatalog
{
    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,32}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TokenDomainRegex();

    [GeneratedRegex(@"\A(?<name>[a-z][a-z0-9_]*(?: [a-z][a-z0-9_]*)*?)(?:\s*\(.*\))?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex TypeNameRegex();

    private static readonly FrozenSet<string> TextTypeNames = new[]
    {
        "text", "varchar", "nvarchar", "char", "nchar", "character varying", "character",
        "varchar2", "nvarchar2", "bpchar", "string", "clob", "nclob", "name", "citext"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> NumericTypeNames = new[]
    {
        "int", "integer", "int2", "int4", "int8", "bigint", "smallint", "tinyint", "hugeint",
        "utinyint", "usmallint", "uinteger", "ubigint", "numeric", "decimal", "number",
        "money", "smallmoney", "float", "float4", "float8", "real", "double", "double precision",
        "binary_float", "binary_double"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> DateOrTimestampTypeNames = new[]
    {
        "date", "datetime", "datetime2", "datetimeoffset", "smalldatetime", "timestamp",
        "timestamp with time zone", "timestamp without time zone", "timestamp with local time zone",
        "timestamptz", "timestamp_ntz", "timestamp_ltz"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> BooleanTypeNames = new[]
    {
        "bit", "bool", "boolean"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static ClassificationColumnFamily ClassifyColumnType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return ClassificationColumnFamily.BinaryOrOther;
        }

        var match = TypeNameRegex().Match(dataType.Trim());
        if (!match.Success)
        {
            return ClassificationColumnFamily.BinaryOrOther;
        }

        string name = match.Groups["name"].Value.ToLowerInvariant();
        if (TextTypeNames.Contains(name)) return ClassificationColumnFamily.Text;
        if (NumericTypeNames.Contains(name)) return ClassificationColumnFamily.Numeric;
        if (DateOrTimestampTypeNames.Contains(name)) return ClassificationColumnFamily.DateOrTimestamp;
        if (BooleanTypeNames.Contains(name)) return ClassificationColumnFamily.Boolean;
        return ClassificationColumnFamily.BinaryOrOther;
    }

    public static bool IsCompatibleWithType(MaskFunction function, ClassificationColumnFamily family) => function switch
    {
        MaskFunction.Nullify or MaskFunction.Redact or MaskFunction.TypeSafeRedact => true,
        MaskFunction.Hash or MaskFunction.Tokenize => true,
        MaskFunction.Partial or MaskFunction.EmailDomain => family == ClassificationColumnFamily.Text,
        MaskFunction.TruncateDate => family == ClassificationColumnFamily.DateOrTimestamp,
        MaskFunction.BucketNumber or MaskFunction.RoundNumber or MaskFunction.GeoJitter => family == ClassificationColumnFamily.Numeric,
        _ => false
    };

    public static ParsedMaskParameters ParseAndValidate(MaskFunction function, string? paramsJson, string? explicitKeyRef = null)
    {
        if (!TryParseAndValidate(function, paramsJson, out var parsed, out string? error, explicitKeyRef))
        {
            throw new ArgumentException(error ?? $"Invalid parameters for mask function '{function}'.", nameof(paramsJson));
        }

        return parsed;
    }

    public static bool TryParseAndValidate(
        MaskFunction function,
        string? paramsJson,
        out ParsedMaskParameters parsed,
        out string? error,
        string? explicitKeyRef = null)
    {
        parsed = new ParsedMaskParameters();
        error = null;

        if (!Enum.IsDefined(function))
        {
            error = $"Unknown mask function '{function}'.";
            return false;
        }

        JsonDocument? doc = null;
        JsonElement root = default;
        try
        {
            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                doc = JsonDocument.Parse(paramsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "Mask parameters JSON must be a JSON object.";
                    return false;
                }

                root = doc.RootElement;
            }

            return TryValidateCore(function, root, doc is not null, explicitKeyRef, out parsed, out error);
        }
        catch (JsonException ex)
        {
            error = $"Mask parameters JSON is malformed: {ex.Message}";
            return false;
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private static bool TryValidateCore(
        MaskFunction function,
        JsonElement root,
        bool hasJson,
        string? explicitKeyRef,
        out ParsedMaskParameters parsed,
        out string? error)
    {
        parsed = new ParsedMaskParameters();
        error = null;

        switch (function)
        {
            case MaskFunction.Nullify:
            case MaskFunction.EmailDomain:
                return true;

            case MaskFunction.Redact:
            case MaskFunction.TypeSafeRedact:
            {
                if (hasJson && TryGetPropertyIgnoreCase(root, "replacement", out var repEl) && repEl.ValueKind != JsonValueKind.Null)
                {
                    if (repEl.ValueKind != JsonValueKind.String)
                    {
                        error = "replacement must be a string.";
                        return false;
                    }

                    string? rep = repEl.GetString();
                    if (rep is { Length: > 128 })
                    {
                        error = "replacement must be at most 128 characters.";
                        return false;
                    }

                    parsed = parsed with { Replacement = string.IsNullOrWhiteSpace(rep) ? "***" : rep };
                }

                return true;
            }

            case MaskFunction.Partial:
            {
                int keepFirst = 1;
                int keepLast = 0;
                char maskChar = '*';

                if (hasJson)
                {
                    if (!TryReadOptionalInt(root, "keep_first", "keep_prefix", out int? kf, out error)) return false;
                    if (kf.HasValue) keepFirst = kf.Value;

                    if (!TryReadOptionalInt(root, "keep_last", "keep_suffix", out int? kl, out error)) return false;
                    if (kl.HasValue) keepLast = kl.Value;

                    if (TryGetPropertyIgnoreCase(root, "mask_char", out var mcEl) && mcEl.ValueKind != JsonValueKind.Null)
                    {
                        if (mcEl.ValueKind != JsonValueKind.String)
                        {
                            error = "mask_char must be a single character string.";
                            return false;
                        }

                        string? mc = mcEl.GetString();
                        if (string.IsNullOrEmpty(mc) || mc.Length != 1 || mc[0] < 0x20 || mc[0] > 0x7E || mc[0] is '\'' or '\\')
                        {
                            error = "mask_char must be a single printable ASCII character excluding quote and backslash.";
                            return false;
                        }

                        maskChar = mc[0];
                    }
                }

                if (keepFirst < 0 || keepLast < 0 || keepFirst + keepLast > MaskingRule.MaxRevealedChars)
                {
                    error = $"PARTIAL mask cannot reveal more than {MaskingRule.MaxRevealedChars} characters (keep_first + keep_last = {keepFirst + keepLast}).";
                    return false;
                }

                parsed = parsed with { KeepFirst = keepFirst, KeepLast = keepLast, MaskChar = maskChar };
                return true;
            }

            case MaskFunction.Hash:
            {
                string? keyRef = ResolveKeyRef(root, hasJson, explicitKeyRef);
                parsed = parsed with { KeyRef = keyRef };
                return true;
            }

            case MaskFunction.Tokenize:
            {
                string? keyRef = ResolveKeyRef(root, hasJson, explicitKeyRef);
                string domain = "DEFAULT";
                if (hasJson && (TryGetPropertyIgnoreCase(root, "domain", out var domEl) || TryGetPropertyIgnoreCase(root, "token_domain", out domEl)) && domEl.ValueKind != JsonValueKind.Null)
                {
                    if (domEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(domEl.GetString()))
                    {
                        error = "TOKENIZE domain must be a non-empty string.";
                        return false;
                    }

                    domain = domEl.GetString()!.Trim().ToUpperInvariant();
                    if (!TokenDomainRegex().IsMatch(domain))
                    {
                        error = "TOKENIZE domain may only contain ASCII letters, digits, '-' or '_' (max 32 characters).";
                        return false;
                    }
                }

                parsed = parsed with { KeyRef = keyRef, TokenDomain = domain };
                return true;
            }

            case MaskFunction.TruncateDate:
            {
                MaskDateTruncateUnit unit = MaskDateTruncateUnit.Month;
                if (hasJson && TryGetPropertyIgnoreCase(root, "unit", out var unitEl) && unitEl.ValueKind != JsonValueKind.Null)
                {
                    if (unitEl.ValueKind != JsonValueKind.String)
                    {
                        error = "TRUNCATE_DATE unit must be YEAR, MONTH, or DAY.";
                        return false;
                    }

                    string? rawUnit = unitEl.GetString()?.Trim().ToUpperInvariant();
                    switch (rawUnit)
                    {
                        case "YEAR": unit = MaskDateTruncateUnit.Year; break;
                        case "MONTH": unit = MaskDateTruncateUnit.Month; break;
                        case "DAY": unit = MaskDateTruncateUnit.Day; break;
                        default:
                            error = $"Unsupported TRUNCATE_DATE unit '{rawUnit}'. Only YEAR, MONTH, or DAY are permitted.";
                            return false;
                    }
                }

                parsed = parsed with { TruncateUnit = unit };
                return true;
            }

            case MaskFunction.BucketNumber:
            {
                if (!hasJson || !TryGetPropertyIgnoreCase(root, "width", out var widthEl) || widthEl.ValueKind != JsonValueKind.Number)
                {
                    error = "BUCKET_NUMBER requires a positive numeric 'width' parameter.";
                    return false;
                }

                if (!widthEl.TryGetDecimal(out decimal width) || width <= 0m || width > 1_000_000_000m)
                {
                    error = "BUCKET_NUMBER 'width' must be a positive finite decimal up to 1,000,000,000.";
                    return false;
                }

                parsed = parsed with { BucketWidth = width };
                return true;
            }

            case MaskFunction.RoundNumber:
            {
                int digits = 0;
                if (hasJson && TryGetPropertyIgnoreCase(root, "digits", out var digitsEl) && digitsEl.ValueKind != JsonValueKind.Null)
                {
                    if (digitsEl.ValueKind != JsonValueKind.Number || !digitsEl.TryGetInt32(out digits) || digits is < -15 or > 15)
                    {
                        error = "ROUND_NUMBER 'digits' must be an integer between -15 and 15.";
                        return false;
                    }
                }

                parsed = parsed with { RoundDigits = digits };
                return true;
            }

            case MaskFunction.GeoJitter:
            {
                int decimals = 2;
                if (hasJson && TryGetPropertyIgnoreCase(root, "decimals", out var decEl) && decEl.ValueKind != JsonValueKind.Null)
                {
                    if (decEl.ValueKind != JsonValueKind.Number || !decEl.TryGetInt32(out decimals) || decimals is < 0 or > 6)
                    {
                        error = "GEO_JITTER 'decimals' must be an integer between 0 and 6.";
                        return false;
                    }
                }

                parsed = parsed with { GeoDecimals = decimals };
                return true;
            }

            default:
                error = $"Unknown mask function '{function}'.";
                return false;
        }
    }

    private static string? ResolveKeyRef(JsonElement root, bool hasJson, string? explicitKeyRef)
    {
        if (!string.IsNullOrWhiteSpace(explicitKeyRef))
        {
            return explicitKeyRef.Trim();
        }

        if (hasJson && TryGetPropertyIgnoreCase(root, "key_ref", out var el) && el.ValueKind == JsonValueKind.String)
        {
            var raw = el.GetString()?.Trim();
            return string.IsNullOrEmpty(raw) ? null : raw;
        }

        return null;
    }

    private static bool TryReadOptionalInt(JsonElement root, string primaryName, string aliasName, out int? value, out string? error)
    {
        value = null;
        error = null;
        if (!TryGetPropertyIgnoreCase(root, primaryName, out var el) && !TryGetPropertyIgnoreCase(root, aliasName, out el))
        {
            return true;
        }

        if (el.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int parsed))
        {
            error = $"'{primaryName}' must be a valid 32-bit integer.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static string NormalizeCatalogType(string dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return string.Empty;
        }

        var match = TypeNameRegex().Match(dataType.Trim());
        return match.Success ? match.Groups["name"].Value.ToLowerInvariant() : dataType.Trim().ToLowerInvariant();
    }

    public static decimal Pow10Decimal(int exponent)
    {
        decimal result = 1m;
        for (int i = 0; i < exponent; i++) result *= 10m;
        return result;
    }
}
