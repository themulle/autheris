namespace Autheris.Tests.Unit.Databricks;

using System;
using System.Data;
using System.Data.Common;
using System.Text;
using Autheris.Infrastructure.Databricks;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1140: the chunked string read of the result byte budget (<c>ResultByteBudget.ReadValue</c>, #398) calls
/// <see cref="DbDataReader.GetChars"/> on string columns. <see cref="DataTableReader.GetChars"/> only streams <c>char[]</c> cells
/// and throws <see cref="InvalidCastException"/> for the string cells the Databricks reader stores, so every GraphQL
/// aggregation with a string group key on Databricks failed. The reader streams string cells itself.
/// </summary>
public sealed class DatabricksDataReaderGetCharsTests
{
    private static DatabricksDataReader ReaderWith(params object?[] values)
    {
        var table = new DataTable();
        table.Columns.Add("Cost_Center", typeof(string));
        foreach (var value in values)
        {
            table.Rows.Add(value ?? DBNull.Value);
        }

        return new DatabricksDataReader(table, [("Cost_Center", new DatabricksColumn("Cost_Center", "STRING"), typeof(string))]);
    }

    [Fact]
    public void GetChars_streams_a_string_cell_in_chunks()
    {
        using var reader = ReaderWith("CC-4711-Controlling");
        reader.Read().ShouldBeTrue();

        var sb = new StringBuilder();
        var buffer = new char[4];
        long read;
        while ((read = reader.GetChars(0, sb.Length, buffer, 0, buffer.Length)) > 0)
        {
            sb.Append(buffer, 0, (int)read);
        }

        sb.ToString().ShouldBe("CC-4711-Controlling");
    }

    [Fact]
    public void GetChars_with_a_null_buffer_returns_the_length()
    {
        using var reader = ReaderWith("abcdef");
        reader.Read().ShouldBeTrue();

        reader.GetChars(0, 0, null, 0, 0).ShouldBe(6);
    }

    [Fact]
    public void GetChars_respects_the_buffer_offset_and_the_end_of_the_value()
    {
        using var reader = ReaderWith("abcdef");
        reader.Read().ShouldBeTrue();

        var buffer = new char[8];
        reader.GetChars(0, 4, buffer, 2, 6).ShouldBe(2);
        new string(buffer, 2, 2).ShouldBe("ef");
        reader.GetChars(0, 6, buffer, 0, 4).ShouldBe(0);
    }

    [Fact]
    public void GetChars_rejects_invalid_arguments_like_the_ado_net_contract()
    {
        using var reader = ReaderWith("abc");
        reader.Read().ShouldBeTrue();

        Should.Throw<ArgumentOutOfRangeException>(() => reader.GetChars(0, -1, new char[2], 0, 2));
        Should.Throw<ArgumentOutOfRangeException>(() => reader.GetChars(0, 0, new char[2], 1, 2));
    }

    [Fact]
    public void GetChars_on_a_null_cell_throws_like_other_typed_getters()
    {
        using var reader = ReaderWith((object?)null);
        reader.Read().ShouldBeTrue();

        reader.IsDBNull(0).ShouldBeTrue();
        Should.Throw<InvalidCastException>(() => reader.GetChars(0, 0, new char[2], 0, 2));
    }
}
