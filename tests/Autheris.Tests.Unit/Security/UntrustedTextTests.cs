using Autheris.Domain.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>PLAN-GOV-158-INC1 decision 2, SEC-GOV158I1-01/02: untrusted free text is normalised and rejected by Unicode category.</summary>
public sealed class UntrustedTextTests
{
    [Fact]
    public void UntrustedText_PlainText_IsKeptAndTrimmed()
    {
        var check = UntrustedText.Normalize("  Sales analysts, read-only  ", 500, allowLineFeed: false);

        check.IsValid.ShouldBeTrue();
        check.Value.ShouldBe("Sales analysts, read-only");
    }

    [Fact]
    public void UntrustedText_NfcNormalises()
    {
        var check = UntrustedText.Normalize("Cafe\u0301", 500, allowLineFeed: false);

        check.Value.ShouldBe("Caf\u00e9");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UntrustedText_WhitespaceOnly_IsNull(string? raw)
    {
        var check = UntrustedText.Normalize(raw, 500, allowLineFeed: false);

        check.IsValid.ShouldBeTrue();
        check.Value.ShouldBeNull();
    }

    [Fact]
    public void UntrustedText_OverCap_Rejected()
    {
        UntrustedText.Normalize(new string('a', 501), 500, allowLineFeed: false).IsValid.ShouldBeFalse();
        UntrustedText.Normalize(new string('a', 500), 500, allowLineFeed: false).IsValid.ShouldBeTrue();
        // Length is measured after trim.
        UntrustedText.Normalize("  " + new string('a', 500) + "  ", 500, allowLineFeed: false).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void UntrustedText_BracesLiteral()
    {
        UntrustedText.Normalize("Use {0} and {{x}}", 500, allowLineFeed: false).Value.ShouldBe("Use {0} and {{x}}");
    }

    [Fact]
    public void UntrustedText_LoneSurrogate_Rejected()
    {
        UntrustedText.Normalize("ab\ud800cd", 500, allowLineFeed: false).IsValid.ShouldBeFalse();
        UntrustedText.Normalize("ok \U0001f600", 500, allowLineFeed: false).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void UntrustedText_LineFeed_OnlyWhenAllowed()
    {
        UntrustedText.Normalize("a\nb", 500, allowLineFeed: false).IsValid.ShouldBeFalse();
        UntrustedText.Normalize("a\nb", 500, allowLineFeed: true).Value.ShouldBe("a\nb");
        UntrustedText.Normalize("a\r\nb", 500, allowLineFeed: true).IsValid.ShouldBeFalse();
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("a\u0000b")]          // NUL (Cc)
    [InlineData("a\u0007b")]          // C0 control
    [InlineData("a\u0085b")]          // C1 NEL
    [InlineData("a\tb")]              // TAB
    [InlineData("a\u202eb")]          // RLO (bidi)
    [InlineData("a\u2066b")]          // LRI (bidi isolate)
    [InlineData("a\u200bb")]          // zero width space
    [InlineData("a\u200db")]          // ZWJ
    [InlineData("a\u2060b")]          // word joiner
    [InlineData("a\ufeffb")]          // BOM / ZWNBSP
    [InlineData("a\u200eb")]          // LRM
    [InlineData("a\u200fb")]          // RLM
    [InlineData("a\u061cb")]          // Arabic letter mark
    [InlineData("a\u00adb")]          // soft hyphen
    [InlineData("a\u180eb")]          // Mongolian vowel separator
    [InlineData("a\ufff9b")]          // interlinear annotation anchor
    [InlineData("a\u034fb")]          // combining grapheme joiner
    [InlineData("a\ufe0fb")]          // variation selector 16
    [InlineData("a\u2028b")]          // Zl
    [InlineData("a\u2029b")]          // Zp
    [InlineData("a\u00a0b")]          // NBSP (Zs)
    [InlineData("a\u3000b")]          // ideographic space (Zs)
    [InlineData("a\ue000b")]          // private use (Co)
    [InlineData("a\u0378b")]          // unassigned (Cn)
    public void UntrustedText_RejectsBidiZeroWidthControlNul_AndFormatCategories(string raw)
    {
        // T-GOV158I1-01: rejected by Unicode category after NFC.
        UntrustedText.Normalize(raw, 500, allowLineFeed: false).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void UntrustedText_OrdinaryUnicodeLetters_Accepted()
    {
        UntrustedText.Normalize("Umsatz \u00fcber Gr\u00f6\u00dfe \u65e5\u672c\u8a9e \U0001f600", 500, allowLineFeed: false).IsValid.ShouldBeTrue();
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(0xE0041)] // tag character
    [InlineData(0xE0100)] // variation selector supplement
    public void UntrustedText_RejectsTagCharsAndSupplementaryVariationSelectors(int codePoint)
    {
        UntrustedText.Normalize("a" + char.ConvertFromUtf32(codePoint) + "b", 500, allowLineFeed: false).IsValid.ShouldBeFalse();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void UntrustedText_HugeRaw_RejectedBeforeNormalization_NoEcho()
    {
        // T-GOV158I1-02 (SEC-GOV158I1-02): raw input beyond 4 x cap is refused without normalising and without echoing the text.
        var raw = new string('x', 4 * 500 + 1);

        var check = UntrustedText.Normalize(raw, 500, allowLineFeed: false);

        check.IsValid.ShouldBeFalse();
        check.Failure.ShouldBe(TextCheckFailure.TooLong);
        check.Value.ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void UntrustedText_ControlAfterTrim_Rejected()
    {
        UntrustedText.Normalize(" \u202e ", 500, allowLineFeed: false).IsValid.ShouldBeFalse();
    }
}

public sealed class ConsentDescriptionTextTests
{
    [Fact]
    public void ForOutput_ValidValue_ReturnedNullStaysNull()
    {
        ConsentDescriptionText.ForOutput("Sales analysts", 500).ShouldBe("Sales analysts");
        ConsentDescriptionText.ForOutput(null, 500).ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("evil‮text", 500)]
    [InlineData("0123456789", 5)]
    public void ForOutput_BidiOrOverCap_ReturnsNull(string stored, int cap) =>
        ConsentDescriptionText.ForOutput(stored, cap).ShouldBeNull();
}
