using System.Text.Json;
using Autheris.Application.Sql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>JSON parameter values of WebSQL requests are converted to bindable CLR scalars.</summary>
public class WebSqlParameterValuesTests
{
    private static object? Parse(string json) => WebSqlParameterValues.Normalize(JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Numbers_Strings_Booleans_AndNull_AreConverted()
    {
        Parse("27110722").ShouldBe(27110722L);
        Parse("1.5").ShouldBe(1.5m);
        Parse("\"abc\"").ShouldBe("abc");
        Parse("true").ShouldBe(true);
        Parse("false").ShouldBe(false);
        Parse("null").ShouldBeNull();
    }

    [Fact]
    public void NonJsonValues_PassThrough()
    {
        WebSqlParameterValues.Normalize(42).ShouldBe(42);
        WebSqlParameterValues.Normalize("x").ShouldBe("x");
        WebSqlParameterValues.Normalize(null).ShouldBeNull();
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("{\"a\":1}")]
    public void ArraysAndObjects_AreRejected(string json)
    {
        Should.Throw<ArgumentException>(() => Parse(json));
    }
}
