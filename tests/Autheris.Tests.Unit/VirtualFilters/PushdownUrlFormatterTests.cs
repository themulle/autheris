namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using Autheris.Application.VirtualFilters.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class PushdownUrlFormatterTests
{
    [Fact]
    public void FormatQueryString_CommaSeparated_FormatsCorrectly()
    {
        var keys = new[] { "101", "102", "105" };
        var query = PushdownUrlFormatter.FormatQueryString("ids", keys, PushdownParameterFormat.CommaSeparated);
        query.ShouldBe("ids=101,102,105");
    }

    [Fact]
    public void FormatQueryString_RepeatedParam_FormatsCorrectly()
    {
        var keys = new[] { "101", "102" };
        var query = PushdownUrlFormatter.FormatQueryString("id", keys, PushdownParameterFormat.RepeatedParam);
        query.ShouldBe("id=101&id=102");
    }

    [Fact]
    public void FormatQueryString_ODataIn_Numeric_FormatsWithoutQuotes()
    {
        var keys = new[] { "101", "102" };
        var query = PushdownUrlFormatter.FormatQueryString("customerId", keys, PushdownParameterFormat.ODataIn);
        query.ShouldBe("$filter=customerId in (101, 102)");
    }

    [Fact]
    public void FormatQueryString_ODataIn_String_EscapesQuotes()
    {
        var keys = new[] { "alpha", "O'Reilly" };
        var query = PushdownUrlFormatter.FormatQueryString("code", keys, PushdownParameterFormat.ODataIn);
        query.ShouldBe("$filter=code in ('alpha', 'O''Reilly')");
    }

    [Fact]
    public void FormatQueryString_EmptyKeys_ReturnsEmpty()
    {
        var query = PushdownUrlFormatter.FormatQueryString("ids", Array.Empty<string>(), PushdownParameterFormat.CommaSeparated);
        query.ShouldBe(string.Empty);
    }

    [Fact]
    public void FormatJsonBatchBody_SerializesValidJsonArray()
    {
        var keys = new[] { "key1", "key\"2" };
        var body = PushdownUrlFormatter.FormatJsonBatchBody("filterIds", keys);
        body.ShouldBe("{\"filterIds\":[\"key1\",\"key\\\"2\"]}");
    }
}
