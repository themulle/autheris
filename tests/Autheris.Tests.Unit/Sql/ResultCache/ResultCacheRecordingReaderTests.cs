namespace Autheris.Tests.Unit.Sql.ResultCache;

using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.ResultCache;
using Shouldly;
using Xunit;

public sealed class ResultCacheRecordingReaderTests
{
    private static async Task<int> DrainAsync(DbDataReader reader)
    {
        var count = 0;
        while (await reader.ReadAsync())
        {
            count++;
        }

        return count;
    }

    private sealed class RejectingReservation : IResultCacheReservation
    {
        public bool TryGrow(long bytes) => false;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Values_equal_the_inner_reader_and_the_result_is_complete()
    {
        using var reader = new ResultCacheRecordingReader(ResultCacheFixtures.Rows(3), 100, 1_000_000);

        var seen = 0;
        while (await reader.ReadAsync())
        {
            reader.GetInt32(0).ShouldBe(seen);
            reader.GetString(1).ShouldBe($"name{seen}");
            reader.GetValue(0).ShouldBe(seen);
            reader.GetFieldValue<string>(1).ShouldBe($"name{seen}");
            reader.IsDBNull(1).ShouldBeFalse();
            seen++;
        }

        reader.EndOfDataObserved.ShouldBeTrue();
        reader.TryBuildResult(ResultCacheFixtures.Info, out var result, out var reason).ShouldBeTrue();
        reason.ShouldBeNull();
        result!.Rows.Length.ShouldBe(3);
        result.Rows[2].ShouldBe([2, "name2"]);
        result.Names.ShouldBe(["id", "name"]);
        result.FieldTypes.ShouldBe([typeof(int), typeof(string)]);
        result.Truncated.ShouldBeFalse();
        result.SizeBytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Records_post_metric_view_filter_rows_and_masks_SECRC05()
    {
        // The reader handed in is the outermost one: masking and group filtering already happened below it.
        var masked = ResultCacheFixtures.Rows(2, i => "***");
        using var reader = new ResultCacheRecordingReader(masked, 100, 1_000_000);
        var delivered = new System.Collections.Generic.List<string>();
        while (await reader.ReadAsync())
        {
            delivered.Add(reader.GetString(1));
        }

        reader.TryBuildResult(ResultCacheFixtures.Info, out var result, out _).ShouldBeTrue();

        delivered.ShouldBe(["***", "***"]);
        result!.Rows.ShouldAllBe(r => (string)r[1]! == "***");
        result.Info.ShouldBeSameAs(ResultCacheFixtures.Info);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Records_when_no_row_limit_SECRC05()
    {
        using var reader = new ResultCacheRecordingReader(new RowLimitedDataReader(ResultCacheFixtures.Rows(5), 0), 100, 1_000_000);

        (await DrainAsync(reader)).ShouldBe(5);

        reader.HasMoreRows.ShouldBeFalse();
        reader.TryBuildResult(ResultCacheFixtures.Info, out var result, out _).ShouldBeTrue();
        result!.Rows.Length.ShouldBe(5);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task Row_limit_truncation_is_recorded_and_delegated()
    {
        using var reader = new ResultCacheRecordingReader(new RowLimitedDataReader(ResultCacheFixtures.Rows(5), 3), 100, 1_000_000);

        (await DrainAsync(reader)).ShouldBe(3);

        reader.HasMoreRows.ShouldBeTrue();
        reader.TryBuildResult(ResultCacheFixtures.Info, out var result, out _).ShouldBeTrue();
        result!.Rows.Length.ShouldBe(3);
        result.Truncated.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Overflow_stops_recording_keeps_streaming_SECRC13()
    {
        using var byRows = new ResultCacheRecordingReader(ResultCacheFixtures.Rows(5), 2, 1_000_000);
        using var byBytes = new ResultCacheRecordingReader(ResultCacheFixtures.Rows(5), 100, 300);

        (await DrainAsync(byRows)).ShouldBe(5);
        (await DrainAsync(byBytes)).ShouldBe(5);

        byRows.Overflowed.ShouldBeTrue();
        byRows.RecordedRows.ShouldBe(0);
        byRows.EndOfDataObserved.ShouldBeTrue();
        byRows.TryBuildResult(ResultCacheFixtures.Info, out var rowsResult, out var rowsReason).ShouldBeFalse();
        rowsResult.ShouldBeNull();
        rowsReason.ShouldBe(WebSqlResultCacheReasons.TooLarge);
        byBytes.TryBuildResult(ResultCacheFixtures.Info, out _, out var bytesReason).ShouldBeFalse();
        bytesReason.ShouldBe(WebSqlResultCacheReasons.TooLarge);
    }

    [Fact]
    public async Task Early_stop_is_incomplete()
    {
        using var reader = new ResultCacheRecordingReader(ResultCacheFixtures.Rows(5), 100, 1_000_000);

        (await reader.ReadAsync()).ShouldBeTrue();

        reader.EndOfDataObserved.ShouldBeFalse();
        reader.TryBuildResult(ResultCacheFixtures.Info, out _, out var reason).ShouldBeFalse();
        reason.ShouldBe(WebSqlResultCacheReasons.Incomplete);
    }

    [Fact]
    public async Task Unsupported_value_type_is_not_recorded()
    {
        var table = new DataTable();
        table.Columns.Add("v", typeof(object));
        table.Rows.Add(new object());
        using var reader = new ResultCacheRecordingReader(table.CreateDataReader(), 100, 1_000_000);

        (await DrainAsync(reader)).ShouldBe(1);

        reader.TryBuildResult(ResultCacheFixtures.Info, out _, out var reason).ShouldBeFalse();
        reason.ShouldBe(WebSqlResultCacheReasons.UnsupportedValueType);
    }

    [Fact]
    public async Task Failed_reservation_stops_recording_but_streams()
    {
        using var reader = new ResultCacheRecordingReader(ResultCacheFixtures.Rows(4), 100, 1_000_000, new RejectingReservation());

        (await DrainAsync(reader)).ShouldBe(4);

        reader.TryBuildResult(ResultCacheFixtures.Info, out _, out var reason).ShouldBeFalse();
        reason.ShouldBe(WebSqlResultCacheReasons.Incomplete);
    }

    [Fact]
    public async Task Null_values_are_replayed_as_db_null()
    {
        var table = new DataTable();
        table.Columns.Add("v", typeof(string));
        table.Rows.Add(DBNull.Value);
        using var reader = new ResultCacheRecordingReader(table.CreateDataReader(), 100, 1_000_000);
        await reader.ReadAsync();

        reader.IsDBNull(0).ShouldBeTrue();
        reader.GetValue(0).ShouldBe(DBNull.Value);
        await DrainAsync(reader);
        reader.TryBuildResult(ResultCacheFixtures.Info, out var result, out _).ShouldBeTrue();
        result!.Rows[0][0].ShouldBe(DBNull.Value);
    }
}
