namespace Autheris.Extensions.Tests;

using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Extensions.OData;
using Shouldly;
using Xunit;

/// <summary>#343: a numeric literal outside the supported range is a clean invalid-query error, never an OverflowException.</summary>
public sealed class ODataFilterNumericRangeTests
{
    [Theory]
    [InlineData("amount eq 567890123456789018901234567890")]
    [InlineData("amount eq 567890123456789018901234567890M")]
    [InlineData("amount eq 567890123456789018901234567890.5")]
    [InlineData("amount eq 99999999999999999999L")]
    [InlineData("amount eq -99999999999999999999L")]
    public void OutOfRangeNumber_Throws_GatewayInvalidQueryException_WithFixedMessage(string filter)
    {
        var ex = Should.Throw<GatewayInvalidQueryException>(() => ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql));

        ex.Message.ShouldBe("Numeric literal in $filter expression is outside the supported range.");
    }

    [Fact]
    public void OverflowingDoubleSuffix_Throws_GatewayInvalidQueryException_WithFixedMessage()
    {
        var ex = Should.Throw<GatewayInvalidQueryException>(() =>
            ODataFilterParser.Parse("amount eq " + new string('9', 400) + "D", DatabaseDialect.PostgreSql));

        ex.Message.ShouldBe("Numeric literal in $filter expression is outside the supported range.");
    }

    [Theory]
    [InlineData("amount eq 12345678901234567890")]
    [InlineData("amount eq 79228162514264337593543950335M")]
    [InlineData("amount eq 9223372036854775807L")]
    public void InRangeNumber_IsAccepted(string filter)
    {
        ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql).ShouldNotBeNull();
    }
}
