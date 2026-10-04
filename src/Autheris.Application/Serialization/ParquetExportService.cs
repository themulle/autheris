namespace Autheris.Application.Serialization;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

/// <summary>
/// F-DATA-01: Serializes already governed result rows into a real Apache Parquet file (Parquet.Net, one row group).
/// The service is a pure output transformation: it only sees rows that already passed RLS, masking, consent and
/// column governance and never reads data on its own.
/// </summary>
public sealed partial class ParquetExportService : IParquetExportService
{
    public const string ParquetContentType = "application/vnd.apache.parquet";

    private const int DefaultMaxRows = 100000;
    private const int MaxColumnNameLength = 256;
    private const int MaxFlattenDepth = 16;

    // Parquet.Net default decimal layout is DECIMAL(38, 18): at most 20 integer digits.
    private const decimal MaxDefaultDecimalMagnitude = 100000000000000000000m;

    private static readonly JsonSerializerOptions NestedJsonOptions = new() { WriteIndented = false };

    [GeneratedRegex(@"^[a-zA-Z0-9_]+$", RegexOptions.Compiled)]
    private static partial Regex SafeIdentifierRegex();

    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<ParquetExportService> _logger;

    private enum ColumnKind
    {
        String,
        Boolean,
        Int32,
        Int64,
        Double,
        Decimal,
        DateTime,
        Binary
    }

    public ParquetExportService(
        IOptions<GatewayOptions> options,
        ILogger<ParquetExportService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ParquetExportResult> ExportToParquetAsync(
        ParquetExportRequest request,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rows);

        var egressOptions = _options.Value.ParquetEgress;
        if (!egressOptions.Enabled)
        {
            throw new InvalidOperationException("Parquet egress export is disabled in gateway configuration.");
        }

        // VULN-02: Path Traversal & CRLF-Injection defense in table and file name
        var tableName = request.Table.TableName;
        if (string.IsNullOrWhiteSpace(tableName) || !SafeIdentifierRegex().IsMatch(tableName))
        {
            throw new ArgumentException($"Invalid or unsafe table name '{tableName}'. Table names must match safe identifier pattern [a-zA-Z0-9_]+.", nameof(request));
        }

        var domainName = request.Table.Domain;
        if (!string.IsNullOrWhiteSpace(domainName) && !SafeIdentifierRegex().IsMatch(domainName))
        {
            throw new ArgumentException($"Invalid or unsafe domain name '{domainName}'. Domain names must match safe identifier pattern [a-zA-Z0-9_]+.", nameof(request));
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
        var suggestedFileName = $"{tableName}_{timestamp}.parquet";

        // VULN-03: Parquet Bomb / Unbounded Memory DoS mitigation
        var configuredMaxRows = egressOptions.MaxRowsPerFile > 0 ? egressOptions.MaxRowsPerFile : DefaultMaxRows;
        var requestedLimit = request.Limit > 0 ? request.Limit : configuredMaxRows;
        var effectiveMaxRows = Math.Min(configuredMaxRows, requestedLimit);

        var isTruncated = rows.Count > effectiveMaxRows;
        var rowCount = isTruncated ? effectiveMaxRows : rows.Count;

        // 1. Normalize rows: nested objects become "parent.child" columns (FlattenNested), lists become JSON strings.
        var normalizedRows = new List<Dictionary<string, object?>>(rowCount);
        var discoveredColumns = new List<string>();
        var discoveredSet = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rowCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var flat = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in rows[i])
            {
                AddNormalizedValue(flat, key, value, request.FlattenNested, 0);
            }

            foreach (var key in flat.Keys)
            {
                if (discoveredSet.Add(key))
                {
                    discoveredColumns.Add(key);
                }
            }

            normalizedRows.Add(flat);
        }

        // 2. Resolve the exported columns (explicit projection or all discovered columns over all rows)
        var columns = ResolveColumns(request.Columns, discoveredColumns, discoveredSet);
        foreach (var col in columns)
        {
            ValidateColumnName(col);
        }

        // 3. Infer one Parquet type per column over all rows and materialize the column arrays
        var fields = new List<DataField>(columns.Count);
        var arrays = new List<Array>(columns.Count);
        var kinds = new List<ColumnKind>(columns.Count);
        foreach (var column in columns)
        {
            var kind = InferKind(normalizedRows, column);
            var (field, data) = BuildColumn(column, kind, normalizedRows);
            fields.Add(field);
            arrays.Add(data);
            kinds.Add(kind);
        }

        // DataFields must be attached to a schema before DataColumns can be created.
        var schema = new ParquetSchema(fields);
        var compression = ResolveCompression(egressOptions.Compression);
        var parquetOptions = new ParquetOptions { CompressionMethod = compression };

        byte[] parquetData;
        using (var ms = new MemoryStream())
        {
            await using (var writer = await ParquetWriter.CreateAsync(schema, ms, parquetOptions, cancellationToken: ct).ConfigureAwait(false))
            {
                // An empty result is a valid Parquet file with the schema and zero row groups.
                if (rowCount > 0)
                {
                    using var rowGroup = writer.CreateRowGroup();
                    for (var i = 0; i < fields.Count; i++)
                    {
                        await WriteColumnAsync(rowGroup, fields[i], kinds[i], arrays[i], ct).ConfigureAwait(false);
                    }
                }
            }

            parquetData = ms.ToArray();
        }

        _logger.LogInformation("Successfully serialized Parquet egress for table '{Table}'. Rows: {Rows} (Truncated: {Truncated}), Columns: {Columns}, Compression: {Compression}, Size: {Size} bytes.",
            request.Table, rowCount, isTruncated, columns.Count, compression, parquetData.Length);

        return new ParquetExportResult(
            Data: parquetData,
            RowCount: rowCount,
            IsTruncated: isTruncated,
            ContentType: ParquetContentType,
            SuggestedFileName: suggestedFileName
        );
    }

    internal static CompressionMethod ResolveCompression(string? configured)
    {
        if (string.Equals(configured, "None", StringComparison.OrdinalIgnoreCase))
        {
            return CompressionMethod.None;
        }

        if (string.Equals(configured, "Gzip", StringComparison.OrdinalIgnoreCase))
        {
            return CompressionMethod.Gzip;
        }

        return CompressionMethod.Snappy;
    }

    private static async Task WriteColumnAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        ColumnKind kind,
        Array data,
        CancellationToken ct)
    {
        switch (kind)
        {
            case ColumnKind.Boolean:
                await rowGroup.WriteAsync<bool>(field, ((bool?[])data).AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                break;
            case ColumnKind.Int32:
                await rowGroup.WriteAsync<int>(field, ((int?[])data).AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                break;
            case ColumnKind.Int64:
                await rowGroup.WriteAsync<long>(field, ((long?[])data).AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                break;
            case ColumnKind.Double:
                await rowGroup.WriteAsync<double>(field, ((double?[])data).AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                break;
            case ColumnKind.Decimal:
                await rowGroup.WriteAsync<decimal>(field, ((decimal?[])data).AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                break;
            case ColumnKind.DateTime:
                await rowGroup.WriteAsync<DateTime>(field, ((DateTime?[])data).AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                break;
            case ColumnKind.Binary:
                await rowGroup.WriteAsync(field, (byte[]?[])data).ConfigureAwait(false);
                break;
            case ColumnKind.String:
            default:
                await rowGroup.WriteAsync(field, (string?[])data).ConfigureAwait(false);
                break;
        }
    }

    private static List<string> ResolveColumns(
        IReadOnlyList<string>? requested,
        List<string> discoveredColumns,
        HashSet<string> discoveredSet)
    {
        if (requested is { Count: > 0 })
        {
            var result = new List<string>(requested.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var col in requested)
            {
                ValidateColumnName(col);

                var matched = false;
                if (discoveredSet.Contains(col))
                {
                    matched = true;
                    if (seen.Add(col))
                    {
                        result.Add(col);
                    }
                }

                // A projected nested column expands to its flattened children ("parent.child").
                var prefix = col + ".";
                foreach (var discovered in discoveredColumns)
                {
                    if (discovered.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        matched = true;
                        if (seen.Add(discovered))
                        {
                            result.Add(discovered);
                        }
                    }
                }

                if (!matched && seen.Add(col))
                {
                    result.Add(col);
                }
            }

            return result;
        }

        if (discoveredColumns.Count > 0)
        {
            return discoveredColumns;
        }

        return ["id"];
    }

    private static void ValidateColumnName(string? column)
    {
        // Parquet schema names are not interpreted (no SQL, no paths). Control characters and oversized names are rejected.
        if (string.IsNullOrWhiteSpace(column) || column.Length > MaxColumnNameLength)
        {
            throw new ArgumentException($"Invalid column identifier. Column names must be non-empty and at most {MaxColumnNameLength} characters long.", nameof(column));
        }

        foreach (var c in column)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException("Invalid column identifier. Column names must not contain control characters.", nameof(column));
            }
        }
    }

    private static void AddNormalizedValue(Dictionary<string, object?> flat, string name, object? value, bool flatten, int depth)
    {
        switch (value)
        {
            case null:
            case DBNull:
                flat[name] = null;
                return;

            case JsonElement element:
                AddJsonElement(flat, name, element, flatten, depth);
                return;

            case string:
            case byte[]:
                flat[name] = value;
                return;

            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                if (flatten && depth < MaxFlattenDepth)
                {
                    foreach (var (childKey, childValue) in readOnlyDictionary)
                    {
                        AddNormalizedValue(flat, $"{name}.{childKey}", childValue, flatten, depth + 1);
                    }
                }
                else
                {
                    flat[name] = SerializeNested(value);
                }
                return;

            case IDictionary<string, object?> dictionary:
                if (flatten && depth < MaxFlattenDepth)
                {
                    foreach (var (childKey, childValue) in dictionary)
                    {
                        AddNormalizedValue(flat, $"{name}.{childKey}", childValue, flatten, depth + 1);
                    }
                }
                else
                {
                    flat[name] = SerializeNested(value);
                }
                return;

            case IEnumerable:
                // Lists / arrays are exported as JSON strings (no repeated Parquet fields).
                flat[name] = SerializeNested(value);
                return;

            default:
                flat[name] = value;
                return;
        }
    }

    private static void AddJsonElement(Dictionary<string, object?> flat, string name, JsonElement element, bool flatten, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (flatten && depth < MaxFlattenDepth)
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        AddJsonElement(flat, $"{name}.{property.Name}", property.Value, flatten, depth + 1);
                    }
                }
                else
                {
                    flat[name] = element.GetRawText();
                }
                return;

            case JsonValueKind.Array:
                flat[name] = element.GetRawText();
                return;

            case JsonValueKind.String:
                flat[name] = element.GetString();
                return;

            case JsonValueKind.Number:
                flat[name] = ConvertJsonNumber(element);
                return;

            case JsonValueKind.True:
                flat[name] = true;
                return;

            case JsonValueKind.False:
                flat[name] = false;
                return;

            default:
                flat[name] = null;
                return;
        }
    }

    private static object ConvertJsonNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var l))
        {
            return l;
        }

        if (element.TryGetDecimal(out var dec))
        {
            return dec;
        }

        if (element.TryGetDouble(out var d) && double.IsFinite(d))
        {
            return d;
        }

        return element.GetRawText();
    }

    private static string SerializeNested(object value) =>
        JsonSerializer.Serialize(value, value.GetType(), NestedJsonOptions);

    private static ColumnKind InferKind(List<Dictionary<string, object?>> rows, string column)
    {
        ColumnKind? kind = null;
        var decimalOverflow = false;

        foreach (var row in rows)
        {
            if (!row.TryGetValue(column, out var value) || value is null)
            {
                continue;
            }

            if (value is decimal dec && Math.Abs(dec) >= MaxDefaultDecimalMagnitude)
            {
                decimalOverflow = true;
            }

            var valueKind = ClassifyValue(value);
            kind = kind is null ? valueKind : Combine(kind.Value, valueKind);
            if (kind == ColumnKind.String)
            {
                break;
            }
        }

        var result = kind ?? ColumnKind.String;
        if (result == ColumnKind.Decimal && decimalOverflow)
        {
            // Values that do not fit DECIMAL(38,18) are exported loss-free as strings.
            result = ColumnKind.String;
        }

        return result;
    }

    private static ColumnKind ClassifyValue(object value) => value switch
    {
        bool => ColumnKind.Boolean,
        byte or sbyte or short or ushort or int => ColumnKind.Int32,
        uint or long => ColumnKind.Int64,
        ulong => ColumnKind.Decimal,
        float or double => ColumnKind.Double,
        decimal => ColumnKind.Decimal,
        DateTime or DateTimeOffset or DateOnly => ColumnKind.DateTime,
        byte[] => ColumnKind.Binary,
        _ => ColumnKind.String
    };

    private static bool IsNumeric(ColumnKind kind) =>
        kind is ColumnKind.Int32 or ColumnKind.Int64 or ColumnKind.Double or ColumnKind.Decimal;

    private static ColumnKind Combine(ColumnKind current, ColumnKind next)
    {
        if (current == next)
        {
            return current;
        }

        if (IsNumeric(current) && IsNumeric(next))
        {
            if (current == ColumnKind.Double || next == ColumnKind.Double)
            {
                return ColumnKind.Double;
            }

            if (current == ColumnKind.Decimal || next == ColumnKind.Decimal)
            {
                return ColumnKind.Decimal;
            }

            return ColumnKind.Int64;
        }

        return ColumnKind.String;
    }

    private static object? GetValue(Dictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) ? value : null;

    private static (DataField Field, Array Data) BuildColumn(string name, ColumnKind kind, List<Dictionary<string, object?>> rows)
    {
        var count = rows.Count;
        switch (kind)
        {
            case ColumnKind.Boolean:
            {
                var data = new bool?[count];
                for (var i = 0; i < count; i++)
                {
                    data[i] = GetValue(rows[i], name) is bool b ? b : null;
                }
                return (new DataField(name, typeof(bool?)), data);
            }

            case ColumnKind.Int32:
            {
                var data = new int?[count];
                for (var i = 0; i < count; i++)
                {
                    var value = GetValue(rows[i], name);
                    data[i] = value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
                return (new DataField(name, typeof(int?)), data);
            }

            case ColumnKind.Int64:
            {
                var data = new long?[count];
                for (var i = 0; i < count; i++)
                {
                    var value = GetValue(rows[i], name);
                    data[i] = value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }
                return (new DataField(name, typeof(long?)), data);
            }

            case ColumnKind.Double:
            {
                var data = new double?[count];
                for (var i = 0; i < count; i++)
                {
                    var value = GetValue(rows[i], name);
                    data[i] = value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                return (new DataField(name, typeof(double?)), data);
            }

            case ColumnKind.Decimal:
            {
                var data = new decimal?[count];
                for (var i = 0; i < count; i++)
                {
                    var value = GetValue(rows[i], name);
                    data[i] = value is null ? null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                }
                return (new DataField(name, typeof(decimal?)), data);
            }

            case ColumnKind.DateTime:
            {
                var data = new DateTime?[count];
                for (var i = 0; i < count; i++)
                {
                    var value = GetValue(rows[i], name);
                    data[i] = value is null ? null : ToUtcDateTime(value);
                }
                return (new DateTimeDataField(name, DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Micros, isNullable: true), data);
            }

            case ColumnKind.Binary:
            {
                var data = new byte[]?[count];
                for (var i = 0; i < count; i++)
                {
                    data[i] = GetValue(rows[i], name) as byte[];
                }
                return (new DataField(name, typeof(byte[]), isNullable: true), data);
            }

            default:
            {
                var data = new string?[count];
                for (var i = 0; i < count; i++)
                {
                    var value = GetValue(rows[i], name);
                    data[i] = value is null ? null : ToInvariantString(value);
                }
                return (new DataField(name, typeof(string), isNullable: true), data);
            }
        }
    }

    private static DateTime ToUtcDateTime(object value) => value switch
    {
        DateTimeOffset dto => dto.UtcDateTime,
        DateOnly date => DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
        DateTime { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        DateTime { Kind: DateTimeKind.Unspecified } unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
        DateTime utc => utc,
        _ => throw new InvalidOperationException($"Value of type '{value.GetType().Name}' is not a date/time value.")
    };

    private static string ToInvariantString(object value) => value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("o", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        Guid guid => guid.ToString("D"),
        byte[] bytes => Convert.ToBase64String(bytes),
        Enum e => e.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
