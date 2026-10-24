using Autheris.Tests.Simulation.Infrastructure;
using Shouldly;

namespace Autheris.Tests.Simulation.Meta;

/// <summary>WP0 review follow-ups: duplicate quarantine hashes and line breaks in issue draft cells.</summary>
public sealed class SimWp0FollowUpTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void SimQuarantine_RejectsDuplicateSha256()
    {
        var sha = new string('a', 64);
        var entry = $$"""{"sha256":"{{sha}}","issue":12,"reason":"r","approvedOn":"2026-10-01"}""";

        SimQuarantine.Parse($"[{entry}]").Count.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => SimQuarantine.Parse($"[{entry},{entry.Replace("12", "13", StringComparison.Ordinal)}]")).Message.ShouldContain("unique");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void IssueDraft_CellReplacesCarriageReturnAndLineFeedWithSpace()
    {
        var finding = new SimFinding(FindingKind.WrongResult, "WrongResult:a\r\nb\nc\rd", "q", "e", "a", "r");

        var text = IssueDraft.Render(finding);

        text.ShouldContain("Error class: WrongResult:a  b c d");
        text.Split('\n').Count(l => l.StartsWith("Error class:", StringComparison.Ordinal)).ShouldBe(1);
        text.ShouldNotContain("\r");
    }
}
