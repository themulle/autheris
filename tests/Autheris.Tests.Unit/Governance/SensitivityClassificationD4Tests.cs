namespace Autheris.Tests.Unit.Governance;

using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>D-4 (ADR-010): value table of the highly-sensitive classification.</summary>
public sealed class SensitivityClassificationD4Tests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("PUBLIC", false)]
    [InlineData("low", false)]
    [InlineData("INTERNAL", false)]
    [InlineData("NORMAL", false)]
    [InlineData("MEDIUM", false)]
    [InlineData("CONFIDENTIAL", true)]
    [InlineData("HIGH", true)]
    [InlineData(" high ", true)]
    [InlineData("RESTRICTED", true)]
    [InlineData("SECRET", true)]
    [InlineData("PII", true)]
    [InlineData("SomethingUnknown", true)]
    public void IsSensitivityHigh_ValueTable(string? sensitivity, bool expected)
    {
        Table.IsSensitivityHigh(sensitivity).ShouldBe(expected);
    }

    [Fact]
    public void RequiresFourEyes_MakesAnyTableHighlySensitive()
    {
        new Table { Sensitivity = "LOW", RequiresFourEyes = true }.IsHighlySensitive.ShouldBeTrue();
        new Table { Sensitivity = "MEDIUM" }.IsHighlySensitive.ShouldBeFalse();
    }
}
