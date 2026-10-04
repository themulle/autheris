namespace Autheris.Application.Serialization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-DATA-04: Implementation of high-throughput Apache Arrow IPC columnar streaming.
/// Operates as a pure projection/serializer over already governed and masked result rows.
/// </summary>
public sealed class ArrowExportService : IArrowExportService
{
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<ArrowExportService> _logger;

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

    public ArrowExportService(
        IOptions<GatewayOptions> options,
        ILogger<ArrowExportService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask ExportToStreamAsync(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        Stream outputStream,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(outputStream);

        var rowList = rows as IReadOnlyList<IReadOnlyDictionary<string, object?>> ?? rows.ToList();
        var maxRows = _options.Value.Arrow?.MaxExportRows ?? 1000000;
        if (rowList.Count > maxRows)
        {
            throw new InvalidOperationException($"Export row count ({rowList.Count}) exceeds maximum allowed Arrow export limit of {maxRows}.");
        }

        var batch = BuildRecordBatch(rowList);

        _logger.LogDebug("F-DATA-04 Streaming {RowCount} rows into Arrow IPC Stream", rowList.Count);
        using var writer = new ArrowStreamWriter(outputStream, batch.Schema, leaveOpen: true);
        await writer.WriteRecordBatchAsync(batch, ct).ConfigureAwait(false);
        await outputStream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<byte[]> ExportToBytesAsync(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await ExportToStreamAsync(rows, ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    public RecordBatch BuildRecordBatch(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            var emptySchema = new Schema.Builder().Field(new Field("_empty", StringType.Default, true)).Build();
            var emptyArray = new StringArray.Builder().Build();
            return new RecordBatch(emptySchema, new IArrowArray[] { emptyArray }, 0);
        }

        // 1. Detect Columns & Inferred Types
        var columnNames = rows
            .SelectMany(r => r.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var columnKinds = new Dictionary<string, ColumnKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in columnNames)
        {
            columnKinds[col] = InferColumnKind(rows, col);
        }

        // 2. Build Schema
        var schemaBuilder = new Schema.Builder();
        foreach (var col in columnNames)
        {
            var arrowType = columnKinds[col] switch
            {
                ColumnKind.Int32 => (IArrowType)Int32Type.Default,
                ColumnKind.Int64 => Int64Type.Default,
                ColumnKind.Double => DoubleType.Default,
                ColumnKind.Decimal => new Decimal128Type(38, 18),
                ColumnKind.Boolean => BooleanType.Default,
                ColumnKind.DateTime => new TimestampType(TimeUnit.Microsecond, "UTC"),
                ColumnKind.Binary => BinaryType.Default,
                _ => StringType.Default
            };

            schemaBuilder.Field(new Field(col, arrowType, nullable: true));
        }

        var schema = schemaBuilder.Build();

        // 3. Build Columns
        var arrays = new List<IArrowArray>(columnNames.Count);
        foreach (var col in columnNames)
        {
            var kind = columnKinds[col];
            arrays.Add(BuildColumnArray(rows, col, kind));
        }

        return new RecordBatch(schema, arrays, rows.Count);
    }

    private static ColumnKind InferColumnKind(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string columnName)
    {
        foreach (var row in rows)
        {
            if (row.TryGetValue(columnName, out var val) && val is not null and not DBNull)
            {
                return val switch
                {
                    bool => ColumnKind.Boolean,
                    byte or sbyte or short or ushort or int => ColumnKind.Int32,
                    long or uint or ulong => ColumnKind.Int64,
                    float or double => ColumnKind.Double,
                    decimal => ColumnKind.Decimal,
                    DateTime or DateTimeOffset => ColumnKind.DateTime,
                    byte[] => ColumnKind.Binary,
                    _ => ColumnKind.String
                };
            }
        }

        return ColumnKind.String;
    }

    private static IArrowArray BuildColumnArray(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string columnName,
        ColumnKind kind)
    {
        switch (kind)
        {
            case ColumnKind.Int32:
            {
                var builder = new Int32Array.Builder();
                foreach (var row in rows)
                {
                    if (row.TryGetValue(columnName, out var val) && val is not null and not DBNull)
                    {
                        builder.Append(Convert.ToInt32(val, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.AppendNull();
                    }
                }
                return builder.Build();
            }

            case ColumnKind.Int64:
            {
                var builder = new Int64Array.Builder();
                foreach (var row in rows)
                {
                    if (row.TryGetValue(columnName, out var val) && val is not null and not DBNull)
                    {
                        builder.Append(Convert.ToInt64(val, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.AppendNull();
                    }
                }
                return builder.Build();
            }

            case ColumnKind.Double:
            {
                var builder = new DoubleArray.Builder();
                foreach (var row in rows)
                {
                    if (row.TryGetValue(columnName, out var val) && val is not null and not DBNull)
                    {
                        builder.Append(Convert.ToDouble(val, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.AppendNull();
                    }
                }
                return builder.Build();
            }

            case ColumnKind.Decimal:
            {
                var builder = new Decimal128Array.Builder(new Decimal128Type(38, 18));
                foreach (var row in rows)
                {
                    if (row.TryGetValue(columnName, out var val) && val is not null and not DBNull)
                    {
                        var d = Convert.ToDecimal(val, CultureInfo.InvariantCulture);
                        builder.Append(d);
                    }
                    else
                    {
                        builder.AppendNull();
                    }
                }
                return builder.Build();
            }

            case ColumnKind.Boolean:
            {
                var builder = new BooleanArray.Builder();
                foreach (var row in rows)
                {
                    if (row.TryGetValue(columnName, out var val) && val is not null and not DBNull)
                    {
                        builder.Append(Convert.ToBoolean(val, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.AppendNull();
                    }
                }
                return builder.Build();
            }

            case ColumnKind.DateTime:
            {
                var builder = new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, "UTC"));
                foreach (var row in rows)
                {
                    if (row.TryGetValue(columnName, out var val) && val is not null and not DBNull)
                    {
                        DateTimeOffset dto = val switch
                        {
                            DateTimeOffset dtOffset => dtOffset.ToUniversalTime(),
                            DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                            _ => DateTimeOffset.Parse(val.ToString()!, CultureInfo.InvariantCulture).ToUniversalTime()
                        };
                        builder.Append(dto);
                    }
                    else
                    {
                        builder.AppendNull();
                    }
                }
                return builder.Build();
            }

            case ColumnKind.Binary:
            {
                var builder = new BinaryArray.Builder();
                foreach (var row in rows)
                {
                    if (row.TryGetValue(columnName, out var val) && val is byte[] b)
                    {
                        builder.Append(b);
                    }
                    else
                    {
                        builder.AppendNull();
                    }
                }
                return builder.Build();
            }

            default:
            {
                var builder = new StringArray.Builder();
                foreach (var row in rows)
                {
                    if (row.TryGetValue(columnName, out var val) && val is not null and not DBNull)
                    {
                        builder.Append(val.ToString());
                    }
                    else
                    {
                        builder.AppendNull();
                    }
                }
                return builder.Build();
            }
        }
    }
}
