namespace Autheris.Tests.Unit.Security;

using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed class TenantRegistryKeyTests
{
    [Theory]
    [InlineData("acme", "ACME")]
    [InlineData("Acme", "ACME")]
    [InlineData("ACME", "ACME")]
    [InlineData("a-b_c", "A-B_C")]
    public void Normalize_UsesInvariantUpperCase(string input, string expected) =>
        TenantRegistryKey.Normalize(input).ShouldBe(expected);

    [Fact]
    public void Normalize_AppliesNfkc_SoCompatibilityFormsGroupTogether() =>
        TenantRegistryKey.Normalize("ａｃｍｅ").ShouldBe("ACME"); // fullwidth "acme"
}
