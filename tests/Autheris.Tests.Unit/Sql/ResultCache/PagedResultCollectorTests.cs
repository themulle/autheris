namespace Autheris.Tests.Unit.Sql.ResultCache;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.ResultCache;
using Shouldly;
using Xunit;

public sealed class PagedResultCollectorTests
{
    private static async Task<List<int>> IdsAsync(DbDataReader reader)
    {
        var ids = new List<int>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    private static List<int> Range(int from, int count)
    {
        var list = new List<int>();
        for (var i = from; i < from + count; i++)
        {
            list.Add(i);
        }

        return list;
    }

    [Fact]
    public async Task Complete_result_is_cached_and_any_page_is_sliced()
    {
        var collector = new PagedResultCollector(3, 4, 100, 1_000_000);

        await collector.CollectAsync(ResultCacheFixtures.Rows(10), default);

        collector.Complete.ShouldBeTrue();
        collector.Overflowed.ShouldBeFalse();
        collector.TryBuildFullResult(ResultCacheFixtures.Info, out var full).ShouldBeTrue();
        full!.Rows.Length.ShouldBe(10);
        using var window = collector.CreateWindowReader(ResultCacheFixtures.Info);
        (await IdsAsync(window)).ShouldBe([3, 4, 5, 6]);
        ((IRowLimitProbe)window).HasMoreRows.ShouldBeTrue();
    }

    [Fact]
    public async Task Probe_row_decides_whether_more_rows_exist()
    {
        var exact = new PagedResultCollector(6, 4, 100, 1_000_000);
        var more = new PagedResultCollector(6, 4, 100, 1_000_000);

        await exact.CollectAsync(ResultCacheFixtures.Rows(10), default);
        await more.CollectAsync(ResultCacheFixtures.Rows(11), default);

        using var exactWindow = exact.CreateWindowReader(ResultCacheFixtures.Info);
        using var moreWindow = more.CreateWindowReader(ResultCacheFixtures.Info);
        (await IdsAsync(exactWindow)).ShouldBe([6, 7, 8, 9]);
        ((IRowLimitProbe)exactWindow).HasMoreRows.ShouldBeFalse();
        (await IdsAsync(moreWindow)).ShouldBe([6, 7, 8, 9]);
        ((IRowLimitProbe)moreWindow).HasMoreRows.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(40)]
    public async Task Overflow_by_rows_keeps_the_window_and_stops_at_the_probe_row(int offset)
    {
        var collector = new PagedResultCollector(offset, 5, 10, 1_000_000);
        using var counting = new ResultCacheRecordingReader(ResultCacheFixtures.Rows(50), 1000, 10_000_000);

        await collector.CollectAsync(counting, default);

        collector.Overflowed.ShouldBeTrue();
        collector.OverflowReason.ShouldBe(WebSqlResultCacheReasons.TooLarge);
        collector.Complete.ShouldBeFalse();
        collector.TryBuildFullResult(ResultCacheFixtures.Info, out _).ShouldBeFalse();
        using var window = collector.CreateWindowReader(ResultCacheFixtures.Info);
        (await IdsAsync(window)).ShouldBe(Range(offset, 5));
        ((IRowLimitProbe)window).HasMoreRows.ShouldBeTrue();
        counting.RecordedRows.ShouldBe(Math.Max(offset + 6, 11));
    }

    [Fact]
    public async Task Overflow_near_the_end_ends_with_the_data()
    {
        var collector = new PagedResultCollector(47, 5, 10, 1_000_000);

        await collector.CollectAsync(ResultCacheFixtures.Rows(50), default);

        collector.Overflowed.ShouldBeTrue();
        using var window = collector.CreateWindowReader(ResultCacheFixtures.Info);
        (await IdsAsync(window)).ShouldBe([47, 48, 49]);
        ((IRowLimitProbe)window).HasMoreRows.ShouldBeFalse();
    }

    [Fact]
    public async Task Overflow_by_bytes_still_covers_the_window()
    {
        var collector = new PagedResultCollector(30, 5, 1000, 400);

        await collector.CollectAsync(ResultCacheFixtures.Rows(50), default);

        collector.Overflowed.ShouldBeTrue();
        using var window = collector.CreateWindowReader(ResultCacheFixtures.Info);
        (await IdsAsync(window)).ShouldBe(Range(30, 5));
        ((IRowLimitProbe)window).HasMoreRows.ShouldBeTrue();
    }

    [Fact]
    public async Task Row_limit_truncation_of_a_complete_result_is_reported()
    {
        var collector = new PagedResultCollector(0, 20, 100, 1_000_000);

        await collector.CollectAsync(new RowLimitedDataReader(ResultCacheFixtures.Rows(10), 8), default);

        collector.Complete.ShouldBeTrue();
        using var window = collector.CreateWindowReader(ResultCacheFixtures.Info);
        (await IdsAsync(window)).ShouldBe(Range(0, 8));
        ((IRowLimitProbe)window).HasMoreRows.ShouldBeTrue();
    }

    [Fact]
    public async Task Unsupported_value_type_overflows_with_its_reason()
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("v", typeof(object));
        table.Rows.Add(0, new object());
        var collector = new PagedResultCollector(0, 5, 100, 1_000_000);

        await collector.CollectAsync(table.CreateDataReader(), default);

        collector.Overflowed.ShouldBeTrue();
        collector.OverflowReason.ShouldBe(WebSqlResultCacheReasons.UnsupportedValueType);
        using var window = collector.CreateWindowReader(ResultCacheFixtures.Info);
        (await IdsAsync(window)).ShouldBe([0]);
    }
}
