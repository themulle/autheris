using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>PLAN-GOV-158-INC1 decision 3: the consent description cap is configurable within 50-2000 and fails the start otherwise.</summary>
public sealed class GovernanceTextOptionsTests
{
    private static bool IsValid(GovernanceTextOptions options) =>
        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);

    [Fact]
    public void GovernanceTextOptions_DefaultIs500AndValid()
    {
        var options = new GovernanceTextOptions();

        options.ConsentDescriptionMaxLength.ShouldBe(500);
        GovernanceTextOptions.SectionName.ShouldBe("Governance");
        IsValid(options).ShouldBeTrue();
    }

    [Theory]
    [InlineData(49)]
    [InlineData(2001)]
    [InlineData(0)]
    [InlineData(-1)]
    public void GovernanceTextOptions_OutOfRange_FailsStart(int value) =>
        IsValid(new GovernanceTextOptions { ConsentDescriptionMaxLength = value }).ShouldBeFalse();

    [Theory]
    [InlineData(50)]
    [InlineData(2000)]
    public void GovernanceTextOptions_Boundaries_Valid(int value) =>
        IsValid(new GovernanceTextOptions { ConsentDescriptionMaxLength = value }).ShouldBeTrue();
}
