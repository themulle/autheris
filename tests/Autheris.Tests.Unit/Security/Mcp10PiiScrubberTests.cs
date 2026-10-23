namespace Autheris.Tests.Unit.Security;

using Autheris.Application.Mcp.Services;
using Shouldly;
using Xunit;

/// <summary>
/// MCP-10: the MCP PII scrubber covers IBANs with letters / lowercase IBANs and JSON-escaped e-mail local parts.
/// </summary>
public sealed class Mcp10PiiScrubberTests
{
    [Theory]
    [Trait("Category", "Security")]
    [InlineData("GB82 WEST 1234 5698 7654 32")]
    [InlineData("NL91ABNA0417164300")]
    [InlineData("nl91abna0417164300")]
    [InlineData("DE89 3704 0044 0532 0130 00")]
    public void MCP_10_ValidIbans_AreScrubbed(string iban)
    {
        var result = AiDataGuardrailService.ScrubPiiAndSensitiveData($"{{\"iban\":\"{iban}\"}}", out var modified);

        modified.ShouldBeTrue();
        result.ShouldNotContain(iban);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MCP_10_InvalidChecksum_IsNotTreatedAsIban()
    {
        AiDataGuardrailService.IsValidIban("GB82 WEST 1234 5698 7654 33").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MCP_10_EscapedPlusInLocalPart_IsStillMasked()
    {
        var result = AiDataGuardrailService.ScrubPiiAndSensitiveData("{\"email\":\"john\\u002Btag@x.com\"}", out var modified);

        modified.ShouldBeTrue();
        result.ShouldNotContain("john");
        result.ShouldNotContain("tag@");
    }
}
