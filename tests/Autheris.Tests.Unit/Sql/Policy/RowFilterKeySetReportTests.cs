namespace Autheris.Tests.Unit.Sql.Policy;

using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class RowFilterKeySetReportTests
{
    private static RowFilterKeySetOutcome Outcome(KeySetOutcomeKind kind, KeySetFallbackReason? reason = null, int? count = null)
        => new("d.s.t", null, CorrelatedFilterSource.Consent, RowFilterEvaluationSource.Global, kind, reason, count, count, count is null ? null : 3);

    [Fact]
    public void HasSubstitution_IsTrueOnlyForHitOrMiss()
    {
        var report = new RowFilterKeySetReport();
        report.Add(Outcome(KeySetOutcomeKind.Exists));
        report.Add(Outcome(KeySetOutcomeKind.Fallback, KeySetFallbackReason.Overflow));
        report.HasSubstitution.ShouldBeFalse();

        report.Add(Outcome(KeySetOutcomeKind.Miss, count: 4));

        report.HasSubstitution.ShouldBeTrue();
    }

    [Theory]
    [InlineData(KeySetFallbackReason.BindLimitRetry)]
    [InlineData(KeySetFallbackReason.ExpansionLimitRetry)]
    public void MarkSubstitutedSitesAsRetried_ReplacesOnlySubstitutedSites_D_WPC_4(KeySetFallbackReason reason)
    {
        var report = new RowFilterKeySetReport();
        report.Add(Outcome(KeySetOutcomeKind.Hit, count: 4));
        report.Add(Outcome(KeySetOutcomeKind.Fallback, KeySetFallbackReason.Temporal));
        report.Add(Outcome(KeySetOutcomeKind.Miss, count: 9));

        report.MarkSubstitutedSitesAsRetried(reason);

        report.Outcomes.Select(o => o.Reason).ShouldBe([reason, KeySetFallbackReason.Temporal, reason]);
        report.Outcomes.ShouldAllBe(o => o.Outcome == KeySetOutcomeKind.Fallback && o.KeyCount == null && o.AgeSeconds == null);
        report.HasSubstitution.ShouldBeFalse();
    }

    [Fact]
    public void NewFallbackReasons_AreAppended_ExistingValuesUnchanged_D_WPC_5()
    {
        ((int)KeySetFallbackReason.LoadThrottled).ShouldBe(12);
        ((int)KeySetFallbackReason.ExpansionBudget).ShouldBe(13);
        ((int)KeySetFallbackReason.ExpansionLimitRetry).ShouldBe(14);
        ((int)KeySetFallbackReason.WriteStatement).ShouldBe(15);
        nameof(KeySetFallbackReason.ExpansionBudget).ShouldBe("ExpansionBudget");
    }
}
