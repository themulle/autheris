namespace Autheris.Tests.Unit.Serialization;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Serialization;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Parquet;
using Shouldly;
using Xunit;

public class ParquetExportDateAndNegotiationTests
{
    private static ParquetExportService CreateService()
    {
        var options = Options.Create(new GatewayOptions
        {
            ParquetEgress = new ParquetEgressOptions { Enabled = true, MaxRowsPerFile = 50000 }
        });
        return new ParquetExportService(options, NullLogger<ParquetExportService>.Instance);
    }

    private static async Task<Dictionary<string, object?[]>> ReadParquetColumnsAsync(byte[] data)
    {
        using var stream = new MemoryStream(data);
        await using var reader = await ParquetReader.CreateAsync(stream);
        var result = new Dictionary<string, object?[]>(StringComparer.OrdinalIgnoreCase);
        var fields = reader.Schema.GetDataFields();
        foreach (var field in fields)
        {
            result[field.Name] = [];
        }

        if (reader.RowGroupCount == 0)
        {
            return result;
        }

        using var rowGroup = reader.OpenRowGroupReader(0);
        var rowCount = (int)rowGroup.RowCount;
        foreach (var field in fields)
        {
            if (field.ClrType == typeof(long) || field.ClrType == typeof(long?))
            {
                var mem = new long?[rowCount];
                await rowGroup.ReadAsync(field, mem.AsMemory());
                result[field.Name] = mem.Cast<object?>().ToArray();
            }
            else if (field.ClrType == typeof(DateTime) || field.ClrType == typeof(DateTime?))
            {
                var mem = new DateTime?[rowCount];
                await rowGroup.ReadAsync(field, mem.AsMemory());
                result[field.Name] = mem.Cast<object?>().ToArray();
            }
            else if (field.ClrType == typeof(bool) || field.ClrType == typeof(bool?))
            {
                var mem = new bool?[rowCount];
                await rowGroup.ReadAsync(field, mem.AsMemory());
                result[field.Name] = mem.Cast<object?>().ToArray();
            }
            else if (field.ClrType == typeof(double) || field.ClrType == typeof(double?))
            {
                var mem = new double?[rowCount];
                await rowGroup.ReadAsync(field, mem.AsMemory());
                result[field.Name] = mem.Cast<object?>().ToArray();
            }
            else
            {
                var mem = new string?[rowCount];
                await rowGroup.ReadAsync(field, mem.AsMemory());
                result[field.Name] = mem.Cast<object?>().ToArray();
            }
        }

        return result;
    }

    [Fact]
    public async Task Befund_1_1_ParquetExport_Iso8601Strings_PreservedAsTimestampField()
    {
        var service = CreateService();

        // Simulated rows as emitted by SqlDataSourceExecutor.NormalizeReadValue (ISO-8601 strings)
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["id"] = 1L,
                ["created_at"] = "2026-10-08T15:00:00.0000000Z",
                ["event_date"] = "2026-10-08",
                ["title"] = "Audit Event"
            },
            new Dictionary<string, object?>
            {
                ["id"] = 2L,
                ["created_at"] = "2026-10-08T16:30:00.0000000Z",
                ["event_date"] = "2026-10-09",
                ["title"] = "Review Event"
            }
        };

        var request = new ParquetExportRequest(
            Table: new TableIdentifier("audit", "public", "events"),
            Columns: ["id", "created_at", "event_date", "title"]
        );

        var result = await service.ExportToParquetAsync(request, rows);

        result.ShouldNotBeNull();
        result.RowCount.ShouldBe(2);

        // Verify Parquet file schema and data types
        using var ms = new MemoryStream(result.Data);
        await using var reader = await ParquetReader.CreateAsync(ms);

        var fields = reader.Schema.GetDataFields();
        var createdAtField = fields.FirstOrDefault(f => f.Name == "created_at");
        createdAtField.ShouldNotBeNull();
        // Befund 1.1: created_at must be DateTime (timestamp), NOT string!
        createdAtField.ClrType.ShouldBe(typeof(DateTime));

        var eventDateField = fields.FirstOrDefault(f => f.Name == "event_date");
        eventDateField.ShouldNotBeNull();
        eventDateField.ClrType.ShouldBe(typeof(DateTime));

        var titleField = fields.FirstOrDefault(f => f.Name == "title");
        titleField.ShouldNotBeNull();
        (titleField.ClrType == typeof(string) || titleField.ClrType == typeof(ReadOnlyMemory<char>)).ShouldBeTrue();

        // Read and verify values
        var columns = await ReadParquetColumnsAsync(result.Data);
        var createdVals = columns["created_at"].Cast<DateTime?>().ToArray();
        createdVals[0].HasValue.ShouldBeTrue();
        createdVals[0]!.Value.ToUniversalTime().Year.ShouldBe(2026);
        createdVals[0]!.Value.ToUniversalTime().Hour.ShouldBe(15);
        createdVals[1]!.Value.ToUniversalTime().Hour.ShouldBe(16);
    }

    [Fact]
    public async Task ParquetExport_MixedDateAndNonDateStrings_FallsBackToString()
    {
        var service = CreateService();

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["mixed_col"] = "2026-10-08" },
            new Dictionary<string, object?> { ["mixed_col"] = "regular text string" }
        };

        var request = new ParquetExportRequest(
            Table: new TableIdentifier("sales", "public", "test"),
            Columns: ["mixed_col"]
        );

        var result = await service.ExportToParquetAsync(request, rows);

        using var ms = new MemoryStream(result.Data);
        await using var reader = await ParquetReader.CreateAsync(ms);
        var field = reader.Schema.GetDataFields().First(f => f.Name == "mixed_col");
        (field.ClrType == typeof(string) || field.ClrType == typeof(ReadOnlyMemory<char>)).ShouldBeTrue();
    }
}
