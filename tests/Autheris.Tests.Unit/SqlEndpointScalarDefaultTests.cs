namespace Autheris.Tests.Unit;

using System;
using System.Linq;
using Autheris.Application.SqlEndpoints.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>SQLA-15: scalar defaults of a declarative SQL endpoint are coerced to the declared type at load.</summary>
public sealed class SqlEndpointScalarDefaultTests
{
    private static object? Default(string declaration)
    {
        using var loader = new SqlEndpointLoader(new InMemorySqlEndpointRegistry());
        var def = loader.ParseSqlContent($"-- @name q\n-- @param p: {declaration}\nSELECT id FROM t WHERE id = @p", "fallback");
        return def.Parameters.Single(x => x.Name == "p").DefaultValue;
    }

    [Fact]
    public void Defaults_AreConvertedToTheDeclaredClrType()
    {
        Default("guid = \"0F8FAD5B-D9CB-469F-A165-70867728950E\"").ShouldBe(Guid.Parse("0F8FAD5B-D9CB-469F-A165-70867728950E"));
        Default("double = 1.5").ShouldBe(1.5);
        Default("date = \"2024-01-01\"").ShouldBe(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Default("int = 7").ShouldBe(7);
    }

    [Theory]
    [InlineData("guid = \"nope\"")]
    [InlineData("date = \"not-a-date\"")]
    [InlineData("double = abc")]
    [InlineData("int = 1x")]
    public void Defaults_ThatDoNotConvert_RefuseTheFile(string declaration) =>
        Should.Throw<FormatException>(() => Default(declaration));
}
