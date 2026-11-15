namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Model;

public sealed record DiscoveryArtifactValidationResult(
    bool IsValid,
    string? ViolationCode,
    string? Message,
    string? SamplesJson,
    string? StatisticsJson,
    string ContentSha256,
    int SampleRowCount,
    int StatisticsColumnCount);

/// <summary>
/// Strict validator and canonicalizer for pre-computed discovery artifacts (samples + column/table statistics).
/// Enforces size caps before deserialization, schema version, column existence, non-hidden columns,
/// scalar-only bounds, and statistical invariants.
/// </summary>
public static class DiscoveryArtifactValidator
{
    public const int MaxScalarStringLength = 1024;
    public const int MaxTopValuesCount = 20;
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        MaxDepth = 32,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    public static DiscoveryArtifactValidationResult ValidateRawUtf8(
        ReadOnlyMemory<byte> rawUtf8,
        TableMetadata table,
        CatalogDiscoverySettings settings)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(settings);

        var maxBytes = Math.Clamp(settings.MaxArtifactBytes, 1L, CatalogDiscoverySettings.HardMaxArtifactBytes);
        if (rawUtf8.Length == 0)
        {
            return Invalid("EMPTY_BODY", "The request body cannot be empty.");
        }

        if (rawUtf8.Length > maxBytes)
        {
            return Invalid("BODY_TOO_LARGE", $"Artifact payload exceeds the configured limit of {maxBytes} bytes.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawUtf8, ParseOptions);
        }
        catch (JsonException)
        {
            return Invalid("INVALID_JSON", "Artifact payload is not valid JSON.");
        }

        using (doc)
        {
            return ValidateDocument(doc.RootElement, table, settings);
        }
    }

    public static DiscoveryArtifactValidationResult ValidateSampleRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        TableMetadata table,
        int maxSampleRows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(table);

        var effectiveMaxRows = Math.Clamp(maxSampleRows, 1, CatalogDiscoverySettings.HardMaxSampleRows);
        if (rows.Count > effectiveMaxRows)
        {
            return Invalid("SAMPLE_ROW_LIMIT_EXCEEDED", $"Sample row count {rows.Count} exceeds limit {effectiveMaxRows}.");
        }

        var columnsByName = BuildColumnMap(table);
        var normalized = new List<Dictionary<string, object?>>(rows.Count);

        foreach (var row in rows)
        {
            if (row is null)
            {
                return Invalid("INVALID_SAMPLE_ROW", "Each sample row must be a JSON object.");
            }

            var cleanRow = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in row)
            {
                if (!ValidateColumn(columnsByName, key, out var col, out var violation, out var message))
                {
                    return Invalid(violation!, message!);
                }

                if (!TryNormalizeScalarObject(value, out var normalizedValue, out var valViolation, out var valMessage))
                {
                    return Invalid(valViolation!, valMessage!);
                }

                cleanRow[col.ColumnName] = normalizedValue;
            }

            normalized.Add(cleanRow);
        }

        var samplesJson = JsonSerializer.Serialize(normalized);
        var sha256 = ComputeContentSha256(SupportedSchemaVersion, samplesJson, statisticsJson: null);
        return new DiscoveryArtifactValidationResult(
            IsValid: true,
            ViolationCode: null,
            Message: null,
            SamplesJson: samplesJson,
            StatisticsJson: null,
            ContentSha256: sha256,
            SampleRowCount: normalized.Count,
            StatisticsColumnCount: 0);
    }

    private static DiscoveryArtifactValidationResult ValidateDocument(
        JsonElement root,
        TableMetadata table,
        CatalogDiscoverySettings settings)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid("INVALID_ARTIFACT_SHAPE", "Artifact root must be a JSON object.");
        }

        if (!TryGetProperty(root, "schemaVersion", out var versionProp) ||
            versionProp.ValueKind != JsonValueKind.Number ||
            !versionProp.TryGetInt32(out var schemaVersion) ||
            schemaVersion != SupportedSchemaVersion)
        {
            return Invalid("INVALID_SCHEMA_VERSION", $"schemaVersion must equal {SupportedSchemaVersion}.");
        }

        var columnsByName = BuildColumnMap(table);
        var effectiveMaxRows = Math.Clamp(settings.MaxSampleRows, 1, CatalogDiscoverySettings.HardMaxSampleRows);

        string? samplesJson = null;
        var sampleRowCount = 0;
        if (TryGetProperty(root, "samples", out var samplesProp) && samplesProp.ValueKind != JsonValueKind.Null)
        {
            if (!ValidateSamplesElement(samplesProp, columnsByName, effectiveMaxRows, out samplesJson, out sampleRowCount, out var violation, out var message))
            {
                return Invalid(violation!, message!);
            }
        }

        string? statisticsJson = null;
        var statisticsColumnCount = 0;
        if (TryGetProperty(root, "statistics", out var statsProp) && statsProp.ValueKind != JsonValueKind.Null)
        {
            if (!ValidateStatisticsElement(statsProp, columnsByName, out statisticsJson, out statisticsColumnCount, out var violation, out var message))
            {
                return Invalid(violation!, message!);
            }
        }

        var sha256 = ComputeContentSha256(SupportedSchemaVersion, samplesJson, statisticsJson);
        return new DiscoveryArtifactValidationResult(
            IsValid: true,
            ViolationCode: null,
            Message: null,
            SamplesJson: samplesJson,
            StatisticsJson: statisticsJson,
            ContentSha256: sha256,
            SampleRowCount: sampleRowCount,
            StatisticsColumnCount: statisticsColumnCount);
    }

    private static bool ValidateSamplesElement(
        JsonElement samplesProp,
        Dictionary<string, TableColumn> columnsByName,
        int maxSampleRows,
        out string? samplesJson,
        out int rowCount,
        out string? violation,
        out string? message)
    {
        samplesJson = null;
        rowCount = 0;
        violation = null;
        message = null;

        if (samplesProp.ValueKind != JsonValueKind.Array)
        {
            violation = "INVALID_SAMPLES";
            message = "samples must be a JSON array.";
            return false;
        }

        var count = samplesProp.GetArrayLength();
        if (count > maxSampleRows)
        {
            violation = "SAMPLE_ROW_LIMIT_EXCEEDED";
            message = $"Sample row count {count} exceeds limit {maxSampleRows}.";
            return false;
        }

        var normalized = new List<Dictionary<string, object?>>(count);
        foreach (var item in samplesProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                violation = "INVALID_SAMPLE_ROW";
                message = "Each sample row must be a JSON object.";
                return false;
            }

            var cleanRow = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in item.EnumerateObject())
            {
                if (!ValidateColumn(columnsByName, prop.Name, out var col, out violation, out message))
                {
                    return false;
                }

                if (!TryReadJsonScalar(prop.Value, out var scalar, out violation, out message))
                {
                    return false;
                }

                cleanRow[col.ColumnName] = scalar;
            }

            normalized.Add(cleanRow);
        }

        rowCount = normalized.Count;
        samplesJson = JsonSerializer.Serialize(normalized);
        return true;
    }

    private static bool ValidateStatisticsElement(
        JsonElement statsProp,
        Dictionary<string, TableColumn> columnsByName,
        out string? statisticsJson,
        out int columnCount,
        out string? violation,
        out string? message)
    {
        statisticsJson = null;
        columnCount = 0;
        violation = null;
        message = null;

        if (statsProp.ValueKind != JsonValueKind.Object)
        {
            violation = "INVALID_STATISTICS";
            message = "statistics must be a JSON object.";
            return false;
        }

        foreach (var prop in statsProp.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "table", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(prop.Name, "columns", StringComparison.OrdinalIgnoreCase))
            {
                violation = "INVALID_STATISTICS";
                message = $"Unsupported statistics section '{prop.Name}'.";
                return false;
            }
        }

        var canonicalStats = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (TryGetProperty(statsProp, "table", out var tableProp) && tableProp.ValueKind != JsonValueKind.Null)
        {
            if (!ValidateTableStatistics(tableProp, columnsByName, out var cleanTable, out violation, out message))
            {
                return false;
            }

            canonicalStats["table"] = cleanTable;
        }

        if (TryGetProperty(statsProp, "columns", out var colsProp) && colsProp.ValueKind != JsonValueKind.Null)
        {
            if (colsProp.ValueKind != JsonValueKind.Object)
            {
                violation = "INVALID_STATISTICS";
                message = "statistics.columns must be a JSON object.";
                return false;
            }

            var cleanColumns = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in colsProp.EnumerateObject())
            {
                if (!ValidateColumn(columnsByName, prop.Name, out var col, out violation, out message))
                {
                    return false;
                }

                if (!ValidateColumnStatistics(prop.Value, out var cleanColStats, out violation, out message))
                {
                    return false;
                }

                cleanColumns[col.ColumnName] = cleanColStats;
            }

            columnCount = cleanColumns.Count;
            canonicalStats["columns"] = cleanColumns;
        }

        statisticsJson = JsonSerializer.Serialize(canonicalStats);
        return true;
    }

    private static bool ValidateTableStatistics(
        JsonElement tableProp,
        Dictionary<string, TableColumn> columnsByName,
        out Dictionary<string, object?> cleanTable,
        out string? violation,
        out string? message)
    {
        cleanTable = new Dictionary<string, object?>(StringComparer.Ordinal);
        violation = null;
        message = null;

        if (tableProp.ValueKind != JsonValueKind.Object)
        {
            violation = "INVALID_TABLE_STATISTICS";
            message = "statistics.table must be a JSON object.";
            return false;
        }

        if (TryGetProperty(tableProp, "approxRowCount", out var rowCountElem) && rowCountElem.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadNonNegativeInt64(rowCountElem, out var count, out violation, out message))
            {
                return false;
            }

            cleanTable["approxRowCount"] = count;
        }

        if (TryGetProperty(tableProp, "sizeBytes", out var sizeElem) && sizeElem.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadNonNegativeInt64(sizeElem, out var sizeBytes, out violation, out message))
            {
                return false;
            }

            cleanTable["sizeBytes"] = sizeBytes;
        }

        if (TryGetProperty(tableProp, "lastUpdatedAt", out var updatedElem) && updatedElem.ValueKind != JsonValueKind.Null)
        {
            if (updatedElem.ValueKind != JsonValueKind.String ||
                updatedElem.GetString() is not { Length: <= 128 } rawTs ||
                !DateTimeOffset.TryParse(rawTs, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTs))
            {
                violation = "INVALID_TIMESTAMP";
                message = "statistics.table.lastUpdatedAt must be a valid ISO 8601 timestamp.";
                return false;
            }

            cleanTable["lastUpdatedAt"] = parsedTs.ToString("O", CultureInfo.InvariantCulture);
        }

        if (TryGetProperty(tableProp, "partitionColumns", out var partElem) && partElem.ValueKind != JsonValueKind.Null)
        {
            if (partElem.ValueKind != JsonValueKind.Array)
            {
                violation = "INVALID_TABLE_STATISTICS";
                message = "statistics.table.partitionColumns must be an array of column names.";
                return false;
            }

            var cols = new List<string>();
            foreach (var item in partElem.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    violation = "INVALID_TABLE_STATISTICS";
                    message = "Each partition column entry must be a non-empty string.";
                    return false;
                }

                if (!ValidateColumn(columnsByName, item.GetString()!, out var col, out violation, out message))
                {
                    return false;
                }

                cols.Add(col.ColumnName);
            }

            cleanTable["partitionColumns"] = cols;
        }

        return true;
    }

    private static bool ValidateColumnStatistics(
        JsonElement colProp,
        out Dictionary<string, object?> cleanStats,
        out string? violation,
        out string? message)
    {
        cleanStats = new Dictionary<string, object?>(StringComparer.Ordinal);
        violation = null;
        message = null;

        if (colProp.ValueKind != JsonValueKind.Object)
        {
            violation = "INVALID_COLUMN_STATISTICS";
            message = "Each column statistics entry must be a JSON object.";
            return false;
        }

        if (TryGetProperty(colProp, "nullable", out var nullableElem) && nullableElem.ValueKind != JsonValueKind.Null)
        {
            if (nullableElem.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                violation = "INVALID_COLUMN_STATISTICS";
                message = "nullable must be a boolean.";
                return false;
            }

            cleanStats["nullable"] = nullableElem.GetBoolean();
        }

        if (TryGetProperty(colProp, "nullRatio", out var ratioElem) && ratioElem.ValueKind != JsonValueKind.Null)
        {
            if (ratioElem.ValueKind != JsonValueKind.Number ||
                !ratioElem.TryGetDouble(out var ratio) ||
                !double.IsFinite(ratio) ||
                ratio < 0.0 ||
                ratio > 1.0)
            {
                violation = "INVALID_NULL_RATIO";
                message = "nullRatio must be a finite number between 0 and 1.";
                return false;
            }

            cleanStats["nullRatio"] = ratio;
        }

        if (TryGetProperty(colProp, "approxDistinctCount", out var distinctElem) && distinctElem.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadNonNegativeInt64(distinctElem, out var distinctCount, out violation, out message))
            {
                return false;
            }

            cleanStats["approxDistinctCount"] = distinctCount;
        }

        int? minLength = null;
        if (TryGetProperty(colProp, "minLength", out var minLenElem) && minLenElem.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadNonNegativeInt32(minLenElem, out var minLen, out violation, out message))
            {
                return false;
            }

            minLength = minLen;
            cleanStats["minLength"] = minLen;
        }

        if (TryGetProperty(colProp, "maxLength", out var maxLenElem) && maxLenElem.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadNonNegativeInt32(maxLenElem, out var maxLen, out violation, out message))
            {
                return false;
            }

            if (minLength.HasValue && maxLen < minLength.Value)
            {
                violation = "INVALID_LENGTH_RANGE";
                message = "maxLength cannot be less than minLength.";
                return false;
            }

            cleanStats["maxLength"] = maxLen;
        }

        if (TryGetProperty(colProp, "minValue", out var minValElem) && minValElem.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadJsonScalar(minValElem, out var minVal, out violation, out message))
            {
                return false;
            }

            cleanStats["minValue"] = minVal;
        }

        if (TryGetProperty(colProp, "maxValue", out var maxValElem) && maxValElem.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadJsonScalar(maxValElem, out var maxVal, out violation, out message))
            {
                return false;
            }

            cleanStats["maxValue"] = maxVal;
        }

        if (TryGetProperty(colProp, "topValues", out var topElem) && topElem.ValueKind != JsonValueKind.Null)
        {
            if (topElem.ValueKind != JsonValueKind.Array)
            {
                violation = "INVALID_COLUMN_STATISTICS";
                message = "topValues must be a JSON array.";
                return false;
            }

            if (topElem.GetArrayLength() > MaxTopValuesCount)
            {
                violation = "TOP_VALUES_LIMIT_EXCEEDED";
                message = $"topValues count {topElem.GetArrayLength()} exceeds limit {MaxTopValuesCount}.";
                return false;
            }

            var topValues = new List<object?>(topElem.GetArrayLength());
            foreach (var item in topElem.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null)
                {
                    violation = "NON_SCALAR_VALUE";
                    message = "topValues cannot contain null entries.";
                    return false;
                }

                if (!TryReadJsonScalar(item, out var scalar, out violation, out message))
                {
                    return false;
                }

                topValues.Add(scalar);
            }

            cleanStats["topValues"] = topValues;
        }

        return true;
    }

    private static bool ValidateColumn(
        Dictionary<string, TableColumn> columnsByName,
        string name,
        out TableColumn col,
        out string? violation,
        out string? message)
    {
        if (string.IsNullOrWhiteSpace(name) || !columnsByName.TryGetValue(name, out var found))
        {
            col = null!;
            violation = "UNKNOWN_COLUMN";
            message = $"Column '{name}' does not exist on the target table.";
            return false;
        }

        if (found.DiscoveryHidden)
        {
            col = null!;
            violation = "HIDDEN_COLUMN";
            message = $"Column '{name}' is marked discovery-hidden and cannot accept artifacts.";
            return false;
        }

        col = found;
        violation = null;
        message = null;
        return true;
    }

    private static bool TryReadNonNegativeInt64(JsonElement elem, out long value, out string? violation, out string? message)
    {
        value = 0;
        if (elem.ValueKind != JsonValueKind.Number || !elem.TryGetInt64(out var parsed))
        {
            violation = "INVALID_COUNT";
            message = "Expected a whole integer value.";
            return false;
        }

        if (parsed < 0)
        {
            violation = "NEGATIVE_COUNT";
            message = "Count fields must be non-negative.";
            return false;
        }

        value = parsed;
        violation = null;
        message = null;
        return true;
    }

    private static bool TryReadNonNegativeInt32(JsonElement elem, out int value, out string? violation, out string? message)
    {
        value = 0;
        if (elem.ValueKind != JsonValueKind.Number || !elem.TryGetInt32(out var parsed))
        {
            violation = "INVALID_COUNT";
            message = "Expected a 32-bit integer value.";
            return false;
        }

        if (parsed < 0)
        {
            violation = "NEGATIVE_COUNT";
            message = "Length fields must be non-negative.";
            return false;
        }

        value = parsed;
        violation = null;
        message = null;
        return true;
    }

    private static bool TryReadJsonScalar(JsonElement elem, out object? scalar, out string? violation, out string? message)
    {
        violation = null;
        message = null;

        switch (elem.ValueKind)
        {
            case JsonValueKind.Null:
                scalar = null;
                return true;
            case JsonValueKind.True:
                scalar = true;
                return true;
            case JsonValueKind.False:
                scalar = false;
                return true;
            case JsonValueKind.Number:
                if (elem.TryGetInt64(out var i64))
                {
                    scalar = i64;
                    return true;
                }

                if (elem.TryGetDecimal(out var dec))
                {
                    scalar = dec;
                    return true;
                }

                if (elem.TryGetDouble(out var dbl) && double.IsFinite(dbl))
                {
                    scalar = dbl;
                    return true;
                }

                scalar = null;
                violation = "NON_SCALAR_VALUE";
                message = "Numeric value is outside supported finite bounds.";
                return false;
            case JsonValueKind.String:
                var s = elem.GetString()!;
                if (s.Length > MaxScalarStringLength)
                {
                    scalar = null;
                    violation = "STRING_TOO_LONG";
                    message = $"Scalar string value exceeds maximum length of {MaxScalarStringLength}.";
                    return false;
                }

                scalar = s;
                return true;
            default:
                scalar = null;
                violation = "NON_SCALAR_VALUE";
                message = "Only scalar JSON values (string, number, boolean, null) are allowed.";
                return false;
        }
    }

    private static bool TryNormalizeScalarObject(object? value, out object? scalar, out string? violation, out string? message)
    {
        violation = null;
        message = null;

        switch (value)
        {
            case null:
                scalar = null;
                return true;
            case bool b:
                scalar = b;
                return true;
            case string s:
                if (s.Length > MaxScalarStringLength)
                {
                    scalar = null;
                    violation = "STRING_TOO_LONG";
                    message = $"Scalar string value exceeds maximum length of {MaxScalarStringLength}.";
                    return false;
                }

                scalar = s;
                return true;
            case int or long or short or byte or sbyte or uint or ushort or ulong or decimal:
                scalar = value;
                return true;
            case float f:
                if (!float.IsFinite(f))
                {
                    scalar = null;
                    violation = "NON_SCALAR_VALUE";
                    message = "Numeric value must be finite.";
                    return false;
                }

                scalar = f;
                return true;
            case double d:
                if (!double.IsFinite(d))
                {
                    scalar = null;
                    violation = "NON_SCALAR_VALUE";
                    message = "Numeric value must be finite.";
                    return false;
                }

                scalar = d;
                return true;
            case JsonElement je:
                return TryReadJsonScalar(je, out scalar, out violation, out message);
            default:
                scalar = null;
                violation = "NON_SCALAR_VALUE";
                message = "Only scalar values are allowed in sample rows.";
                return false;
        }
    }

    private static Dictionary<string, TableColumn> BuildColumnMap(TableMetadata table)
    {
        var map = new Dictionary<string, TableColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in table.Columns)
        {
            map.TryAdd(col.ColumnName, col);
        }

        return map;
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
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

    public static string ComputeContentSha256(int schemaVersion, string? samplesJson, string? statisticsJson)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            schemaVersion,
            samples = samplesJson,
            statistics = statisticsJson
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    private static DiscoveryArtifactValidationResult Invalid(string code, string message) =>
        new(
            IsValid: false,
            ViolationCode: code,
            Message: message,
            SamplesJson: null,
            StatisticsJson: null,
            ContentSha256: string.Empty,
            SampleRowCount: 0,
            StatisticsColumnCount: 0);
}
