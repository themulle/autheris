using Autheris.Tests.SqlCompilerGate.Harness;
using Shouldly;

namespace Autheris.Tests.SqlCompilerGate;

/// <summary>The database-free rules of the differential gate: an unavailable AST leg is a failure, a legacy defect is skipped only while the AST leg is clean.</summary>
public sealed class DifferentialRulesTests
{
    [Fact]
    public void LegacyDefect_WithZeroAstViolations_IsSkipped() =>
        DifferentialRules.IsSkippedLegacyDefect(isLegacyDefect: true, astViolationCount: 0).ShouldBeTrue();

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void LegacyDefect_WithAstViolations_StaysRed(int violations) =>
        DifferentialRules.IsSkippedLegacyDefect(isLegacyDefect: true, astViolationCount: violations).ShouldBeFalse();

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void NoLegacyDefect_IsNeverSkipped(int violations) =>
        DifferentialRules.IsSkippedLegacyDefect(isLegacyDefect: false, astViolationCount: violations).ShouldBeFalse();

    [Fact]
    public void UnavailableAstLeg_IsAFailure_NotASkip()
    {
        var ex = Should.Throw<Xunit.Sdk.XunitException>(() =>
            DifferentialRules.RequireAstLeg("Sqlite", new[] { "ReadOnly: no binder" }));

        ex.Message.ShouldContain("Sqlite");
        ex.Message.ShouldContain("no binder");
    }

    [Fact]
    public void AvailableAstLeg_DoesNotThrow() =>
        Should.NotThrow(() => DifferentialRules.RequireAstLeg("Sqlite", Array.Empty<string>()));
}
