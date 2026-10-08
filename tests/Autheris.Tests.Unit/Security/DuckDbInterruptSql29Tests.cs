namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// SQL2-9: the OLAP timeout interrupts a running DuckDB query (not only between rows), and generator functions and
/// recursive CTEs are rejected also when tables are staged.
/// </summary>
public sealed class DuckDbInterruptSql29Tests
{
    private static OlapTableSource Source(int rows)
    {
        var table = new TableIdentifier("sales", "public", "nums");
        var meta = new TableMetadata { Identifier = table, Columns = [new TableColumn { ColumnName = "id", DataType = "bigint" }] };
        var data = Enumerable.Range(1, rows).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = (long)i }).ToList();
        return new OlapTableSource(table, data, meta);
    }

    [Theory]
    [InlineData("SELECT n.id, r.range FROM nums n, range(1000000000) r")]
    [InlineData("SELECT * FROM nums, generate_series(1, 100000000)")]
    [InlineData("WITH RECURSIVE x(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM x) SELECT count(*) FROM x, nums")]
    public async Task GeneratorsAndRecursion_AreRejected_EvenWithStagedTables(string sql)
    {
        var engine = new DuckDbOlapEngine(new DuckDbOlapOptions { Enabled = true, MaxStagedRowsPerTable = 100, QueryTimeoutSeconds = 30, MaxThreads = 1 }, NullLogger<DuckDbOlapEngine>.Instance);

        await Should.ThrowAsync<SecurityException>(() => engine.ExecuteOlapQueryAsync(new OlapQueryRequest(sql, [Source(10)])));
    }

    [Fact]
    public async Task Timeout_InterruptsLongRunningQuery()
    {
        var engine = new DuckDbOlapEngine(new DuckDbOlapOptions { Enabled = true, MaxStagedRowsPerTable = 1000, QueryTimeoutSeconds = 1, MaxThreads = 1 }, NullLogger<DuckDbOlapEngine>.Instance);
        // About 8 * 10^9 combinations: far longer than the one-second timeout.
        var request = new OlapQueryRequest("SELECT sum(a.id * b.id + c.id * d.id) FROM nums a, nums b, nums c, nums d", [Source(300)]);

        var sw = Stopwatch.StartNew();
        await Should.ThrowAsync<Exception>(() => engine.ExecuteOlapQueryAsync(request));
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }
}
