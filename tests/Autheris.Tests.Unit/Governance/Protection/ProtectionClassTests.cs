using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;

namespace Autheris.Tests.Unit.Governance.Protection;

[Trait("Category", "Security")]
public sealed class ProtectionClassTests
{
    [Theory]
    [InlineData("PUBLIC", ProtectionClass.Public)]
    [InlineData("public", ProtectionClass.Public)]
    [InlineData("  Public  ", ProtectionClass.Public)]
    [InlineData("INTERNAL", ProtectionClass.Internal)]
    [InlineData("internal", ProtectionClass.Internal)]
    [InlineData("\tInternal\t", ProtectionClass.Internal)]
    [InlineData("CONFIDENTIAL", ProtectionClass.Confidential)]
    [InlineData("confidential", ProtectionClass.Confidential)]
    [InlineData("SECRET", ProtectionClass.Secret)]
    [InlineData("sEcReT", ProtectionClass.Secret)]
    public void TryParse_valid_literals_succeeds(string input, ProtectionClass expected)
    {
        var ok = ProtectionClassExtensions.TryParse(input, out var parsed);

        ok.ShouldBeTrue();
        parsed.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("UNKNOWN")]
    [InlineData("RESTRICTED")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("PUBLIC_1")]
    [InlineData("CONFIDENTIAL!")]
    [InlineData("\u017FECRET")]
    public void TryParse_invalid_or_numeric_fails_closed_to_confidential(string? input)
    {
        var ok = ProtectionClassExtensions.TryParse(input, out var parsed);

        ok.ShouldBeFalse();
        parsed.ShouldBe(ProtectionClass.Confidential);
    }

    [Theory]
    [InlineData("PUBLIC", ProtectionClass.Public)]
    [InlineData("  internal ", ProtectionClass.Internal)]
    [InlineData("CONFIDENTIAL", ProtectionClass.Confidential)]
    [InlineData("secret", ProtectionClass.Secret)]
    [InlineData(null, ProtectionClass.Confidential)]
    [InlineData("", ProtectionClass.Confidential)]
    [InlineData("   ", ProtectionClass.Confidential)]
    [InlineData("unknown", ProtectionClass.Confidential)]
    [InlineData("0", ProtectionClass.Confidential)]
    public void ParseOrConfidential_handles_valid_and_invalid_inputs(string? input, ProtectionClass expected)
    {
        ProtectionClassExtensions.ParseOrConfidential(input).ShouldBe(expected);
    }

    [Fact]
    public void IsStricterThan_orders_hierarchy_and_normalizes_invalid_values()
    {
        ProtectionClass.Internal.IsStricterThan(ProtectionClass.Public).ShouldBeTrue();
        ProtectionClass.Confidential.IsStricterThan(ProtectionClass.Internal).ShouldBeTrue();
        ProtectionClass.Secret.IsStricterThan(ProtectionClass.Confidential).ShouldBeTrue();

        ProtectionClass.Public.IsStricterThan(ProtectionClass.Public).ShouldBeFalse();
        ProtectionClass.Confidential.IsStricterThan(ProtectionClass.Confidential).ShouldBeFalse();
        ProtectionClass.Internal.IsStricterThan(ProtectionClass.Confidential).ShouldBeFalse();

        ((ProtectionClass)99).IsStricterThan(ProtectionClass.Internal).ShouldBeTrue();
        ((ProtectionClass)99).IsStricterThan(ProtectionClass.Confidential).ShouldBeFalse();
        ProtectionClass.Secret.IsStricterThan((ProtectionClass)99).ShouldBeTrue();
        ((ProtectionClass)(-1)).IsStricterThan(ProtectionClass.Public).ShouldBeTrue();
    }

    [Fact]
    public void Max_returns_stricter_and_normalizes_invalid_values()
    {
        ProtectionClassExtensions.Max(ProtectionClass.Public, ProtectionClass.Internal).ShouldBe(ProtectionClass.Internal);
        ProtectionClassExtensions.Max(ProtectionClass.Secret, ProtectionClass.Public).ShouldBe(ProtectionClass.Secret);
        ProtectionClassExtensions.Max(ProtectionClass.Confidential, ProtectionClass.Confidential).ShouldBe(ProtectionClass.Confidential);
        ProtectionClassExtensions.Max(ProtectionClass.Public, (ProtectionClass)99).ShouldBe(ProtectionClass.Confidential);
        ProtectionClassExtensions.Max((ProtectionClass)99, ProtectionClass.Secret).ShouldBe(ProtectionClass.Secret);
        ProtectionClassExtensions.Max((ProtectionClass)(-5), ProtectionClass.Internal).ShouldBe(ProtectionClass.Confidential);
    }

    [Theory]
    [InlineData(ProtectionClass.Public, ProtectionClass.Public)]
    [InlineData(ProtectionClass.Internal, ProtectionClass.Internal)]
    [InlineData(ProtectionClass.Confidential, ProtectionClass.Confidential)]
    [InlineData(ProtectionClass.Secret, ProtectionClass.Secret)]
    [InlineData((ProtectionClass)(-1), ProtectionClass.Confidential)]
    [InlineData((ProtectionClass)4, ProtectionClass.Confidential)]
    [InlineData((ProtectionClass)99, ProtectionClass.Confidential)]
    public void NormalizeOrConfidential_clamps_undefined_values_to_confidential(ProtectionClass input, ProtectionClass expected)
    {
        input.NormalizeOrConfidential().ShouldBe(expected);
    }

    [Theory]
    [InlineData(ProtectionClass.Public, "PUBLIC")]
    [InlineData(ProtectionClass.Internal, "INTERNAL")]
    [InlineData(ProtectionClass.Confidential, "CONFIDENTIAL")]
    [InlineData(ProtectionClass.Secret, "SECRET")]
    [InlineData((ProtectionClass)99, "CONFIDENTIAL")]
    [InlineData((ProtectionClass)(-1), "CONFIDENTIAL")]
    public void ToCanonicalName_returns_uppercase_names_and_fails_closed_for_invalid(ProtectionClass input, string expected)
    {
        input.ToCanonicalName().ShouldBe(expected);
    }

    [Theory]
    [InlineData("OPEN", true, CatalogVisibilityMode.Open)]
    [InlineData("  open  ", true, CatalogVisibilityMode.Open)]
    [InlineData("ON_REQUEST", true, CatalogVisibilityMode.OnRequest)]
    [InlineData("on_request", true, CatalogVisibilityMode.OnRequest)]
    [InlineData("OnRequest", true, CatalogVisibilityMode.OnRequest)]
    [InlineData(null, false, CatalogVisibilityMode.OnRequest)]
    [InlineData("", false, CatalogVisibilityMode.OnRequest)]
    [InlineData("0", false, CatalogVisibilityMode.OnRequest)]
    [InlineData("HIDDEN", false, CatalogVisibilityMode.OnRequest)]
    public void CatalogVisibilityMode_TryParse_and_ParseOrOnRequest_fail_closed(
        string? input,
        bool expectedOk,
        CatalogVisibilityMode expected)
    {
        var ok = CatalogVisibilityModeExtensions.TryParse(input, out var parsed);

        ok.ShouldBe(expectedOk);
        parsed.ShouldBe(expected);
        CatalogVisibilityModeExtensions.ParseOrOnRequest(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(CatalogVisibilityMode.Open, CatalogVisibilityMode.Open)]
    [InlineData(CatalogVisibilityMode.OnRequest, CatalogVisibilityMode.OnRequest)]
    [InlineData((CatalogVisibilityMode)99, CatalogVisibilityMode.OnRequest)]
    [InlineData((CatalogVisibilityMode)(-1), CatalogVisibilityMode.OnRequest)]
    public void CatalogVisibilityMode_NormalizeOrOnRequest_clamps_invalid_values(
        CatalogVisibilityMode input,
        CatalogVisibilityMode expected)
    {
        input.NormalizeOrOnRequest().ShouldBe(expected);
    }
}
