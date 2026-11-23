using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Requests;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.ConsentRequests;

[Trait("Category", "Security")]
public sealed class JustificationPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("123456789")]
    public void Nine_chars_rejected(string? raw)
    {
        var ex = Should.Throw<ConsentRequestException>(() => JustificationPolicy.Validate(raw));
        ex.Code.ShouldBe(JustificationPolicy.TooShortErrorCode);
    }

    [Fact]
    public void Whitespace_padding_rejected()
    {
        var ex = Should.Throw<ConsentRequestException>(() => JustificationPolicy.Validate("   abcde   "));
        ex.Code.ShouldBe(JustificationPolicy.TooShortErrorCode);
    }

    [Fact]
    public void Control_chars_not_counted()
    {
        var ex = Should.Throw<ConsentRequestException>(() => JustificationPolicy.Validate("abc\u0000\u0007\t\n\r123"));
        ex.Code.ShouldBe(JustificationPolicy.TooShortErrorCode);
    }

    [Fact]
    public void Over_2000_rejected()
    {
        var ex = Should.Throw<ConsentRequestException>(() => JustificationPolicy.Validate(new string('a', JustificationPolicy.MaxLength + 1)));
        ex.Code.ShouldBe(JustificationPolicy.TooLongErrorCode);
    }

    [Fact]
    public void Valid_10_to_2000_accepted_and_trimmed()
    {
        var normalized = JustificationPolicy.Validate("  1234567890  ");
        normalized.ShouldBe("1234567890");
    }
}
