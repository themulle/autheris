namespace Autheris.Tests.Unit.Audit;

using Autheris.Application.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CallerLabelNormalizerTests
{
    [Theory]
    [InlineData("=cmd|' /C calc'!A0")]
    [InlineData("=1+1")]
    [InlineData("+SUM(A1:A10)")]
    [InlineData("-cmd")]
    [InlineData("@SUM(A1:A10)")]
    [InlineData("\tmalicious")]
    [InlineData("\rmalicious")]
    public void T_SEC_AUD114_01_Normalizer_FormulaPrefixes_EvaluatesToInvalid(string input)
    {
        CallerLabelNormalizer.NormalizeApplication(input).ShouldBe(CallerLabelNormalizer.InvalidSentinel);
        CallerLabelNormalizer.NormalizeVersion(input).ShouldBe(CallerLabelNormalizer.InvalidSentinel);
        CallerLabelNormalizer.NormalizeQueryName(input).ShouldBe(CallerLabelNormalizer.InvalidSentinel);
    }

    [Theory]
    [InlineData("app<script>alert(1)</script>")]
    [InlineData("app; DROP TABLE users;")]
    [InlineData("föö-bär")]
    [InlineData("app\0nullbyte")]
    [InlineData("app/backslash\\test")]
    [InlineData("app!bang")]
    [InlineData("app#hash")]
    public void T_SEC_AUD114_01_Normalizer_MaliciousCharacters_EvaluatesToInvalid(string input)
    {
        CallerLabelNormalizer.NormalizeApplication(input).ShouldBe(CallerLabelNormalizer.InvalidSentinel);
    }

    [Fact]
    public void T_SEC_AUD114_01_Normalizer_LengthTruncation_BoundsLength()
    {
        var longApp = new string('a', 100);
        var normApp = CallerLabelNormalizer.NormalizeApplication(longApp);
        normApp.Length.ShouldBe(CallerLabelNormalizer.MaxApplicationLength);
        normApp.ShouldBe(new string('a', CallerLabelNormalizer.MaxApplicationLength));

        var longVer = new string('1', 50);
        var normVer = CallerLabelNormalizer.NormalizeVersion(longVer);
        normVer.Length.ShouldBe(CallerLabelNormalizer.MaxVersionLength);
        normVer.ShouldBe(new string('1', CallerLabelNormalizer.MaxVersionLength));

        var longQry = new string('q', 80);
        var normQry = CallerLabelNormalizer.NormalizeQueryName(longQry);
        normQry.Length.ShouldBe(CallerLabelNormalizer.MaxQueryNameLength);
        normQry.ShouldBe(new string('q', CallerLabelNormalizer.MaxQueryNameLength));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("UNKNOWN")]
    [InlineData("invalid")]
    [InlineData("InVaLiD")]
    [InlineData("other")]
    [InlineData("OTHER")]
    public void T_SEC_AUD114_02_Normalizer_ReservedSentinels_EvaluatesToInvalid(string input)
    {
        CallerLabelNormalizer.NormalizeApplication(input).ShouldBe(CallerLabelNormalizer.InvalidSentinel);
        CallerLabelNormalizer.NormalizeVersion(input).ShouldBe(CallerLabelNormalizer.InvalidSentinel);
        CallerLabelNormalizer.NormalizeQueryName(input).ShouldBe(CallerLabelNormalizer.InvalidSentinel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t   \n")]
    public void Normalizer_NullOrWhitespace_EvaluatesToUnknown(string? input)
    {
        CallerLabelNormalizer.NormalizeApplication(input).ShouldBe(CallerLabelNormalizer.UnknownSentinel);
        CallerLabelNormalizer.NormalizeVersion(input).ShouldBe(CallerLabelNormalizer.UnknownSentinel);
        CallerLabelNormalizer.NormalizeQueryName(input).ShouldBe(CallerLabelNormalizer.UnknownSentinel);
    }

    [Theory]
    [InlineData("PowerBI Desktop", "powerbi desktop")]
    [InlineData("data_studio-v2.1", "data_studio-v2.1")]
    [InlineData("  Metabase-App  ", "metabase-app")]
    [InlineData("ETL_Pipeline_01", "etl_pipeline_01")]
    public void Normalizer_ValidLabels_AreLowercased(string input, string expected)
    {
        CallerLabelNormalizer.NormalizeApplication(input).ShouldBe(expected);
    }

    [Fact]
    public void Normalizer_StripFormulaPrefix_StripsCorrectly()
    {
        CallerLabelNormalizer.StripFormulaPrefix("=cmd").ShouldBe("cmd");
        CallerLabelNormalizer.StripFormulaPrefix("@SUM").ShouldBe("SUM");
        CallerLabelNormalizer.StripFormulaPrefix("+123").ShouldBe("123");
        CallerLabelNormalizer.StripFormulaPrefix("-test").ShouldBe("test");
        CallerLabelNormalizer.StripFormulaPrefix("normal").ShouldBe("normal");
    }
}
