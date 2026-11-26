namespace Autheris.Tests.Unit.Sql.ResultCache;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;
using Autheris.Application.Sql.ResultCache;
using Shouldly;
using Xunit;

public sealed class CachedResultDataReaderTests
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

    [Fact]
    public async Task Replays_the_whole_result()
    {
        using var reader = new CachedResultDataReader(ResultCacheFixtures.Result(4));

        reader.FieldCount.ShouldBe(2);
        reader.GetName(1).ShouldBe("name");
        reader.GetFieldType(0).ShouldBe(typeof(int));
        reader.GetDataTypeName(1).ShouldBe("nvarchar");
        reader.GetOrdinal("NAME").ShouldBe(1);
        reader.HasRows.ShouldBeTrue();
        (await IdsAsync(reader)).ShouldBe([0, 1, 2, 3]);
        reader.HasMoreRows.ShouldBeFalse();
    }

    [Fact]
    public async Task Replays_a_window_and_reports_rows_after_it()
    {
        using var reader = new CachedResultDataReader(ResultCacheFixtures.Result(10), 3, 4);

        (await IdsAsync(reader)).ShouldBe([3, 4, 5, 6]);

        reader.HasMoreRows.ShouldBeTrue();
        reader.WindowRows.ShouldBe(4);
    }

    [Fact]
    public async Task Last_window_has_no_more_rows()
    {
        using var reader = new CachedResultDataReader(ResultCacheFixtures.Result(10), 8, 4);

        (await IdsAsync(reader)).ShouldBe([8, 9]);

        reader.HasMoreRows.ShouldBeFalse();
    }

    [Fact]
    public async Task Truncated_result_reports_more_rows_even_for_the_last_window()
    {
        using var reader = new CachedResultDataReader(ResultCacheFixtures.Result(3, truncated: true));

        (await IdsAsync(reader)).ShouldBe([0, 1, 2]);

        reader.HasMoreRows.ShouldBeTrue();
    }

    [Fact]
    public async Task Window_beyond_the_rows_is_empty()
    {
        using var reader = new CachedResultDataReader(ResultCacheFixtures.Result(3), 10, 5);

        (await IdsAsync(reader)).ShouldBeEmpty();

        reader.HasRows.ShouldBeFalse();
        reader.HasMoreRows.ShouldBeFalse();
    }

    [Fact]
    public async Task Replay_can_be_repeated_without_changing_the_cached_rows()
    {
        var result = ResultCacheFixtures.Result(2);
        using (var first = new CachedResultDataReader(result))
        {
            await IdsAsync(first);
        }

        using var second = new CachedResultDataReader(result);
        (await IdsAsync(second)).ShouldBe([0, 1]);
    }

    [Fact]
    public async Task Unknown_column_name_throws_and_closed_reader_yields_no_rows()
    {
        using var reader = new CachedResultDataReader(ResultCacheFixtures.Result(2));

        Should.Throw<IndexOutOfRangeException>(() => reader.GetOrdinal("missing"));
        reader.Close();

        reader.IsClosed.ShouldBeTrue();
        (await reader.ReadAsync()).ShouldBeFalse();
    }
}
