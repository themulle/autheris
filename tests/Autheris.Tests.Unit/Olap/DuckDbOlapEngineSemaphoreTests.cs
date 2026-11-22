using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Olap;

public sealed class DuckDbOlapEngineSemaphoreTests
{
    [Fact]
    public async Task ExecuteOlapQueryAsync_WhenCreateDirectoryThrows_ReleasesSemaphoreAndAllowsSubsequentQuery()
    {
        var conflictPath = Path.Combine(Path.GetTempPath(), "autheris_olap_conflict_" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(conflictPath, "occupied");

        try
        {
            var options = new DuckDbOlapOptions
            {
                Enabled = true,
                MaxConcurrentQueries = 1,
                QueryTimeoutSeconds = 5,
                MaxMemory = "256MB",
                MaxStagedRowsPerTable = 100
            };

            var engine = new DuckDbOlapEngine(options, NullLogger<DuckDbOlapEngine>.Instance)
            {
                TempDirectoryRoot = conflictPath
            };

            var table = new TableIdentifier("sales", "public", "items");
            var meta = new TableMetadata
            {
                Identifier = table,
                Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
            };
            var request = new OlapQueryRequest(
                Sql: "SELECT COUNT(*) AS cnt FROM items",
                Sources: [new OlapTableSource(table, new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["id"] = 1 } }, meta)]);

            await Should.ThrowAsync<IOException>(() => engine.ExecuteOlapQueryAsync(request));
            engine.AvailableConcurrencySlots.ShouldBe(1);

            File.Delete(conflictPath);

            var result = await engine.ExecuteOlapQueryAsync(request);
            result.Rows.Count.ShouldBe(1);
            engine.AvailableConcurrencySlots.ShouldBe(1);
        }
        finally
        {
            if (File.Exists(conflictPath))
            {
                try { File.Delete(conflictPath); } catch { }
            }

            if (Directory.Exists(conflictPath))
            {
                try { Directory.Delete(conflictPath, recursive: true); } catch { }
            }
        }
    }
}
