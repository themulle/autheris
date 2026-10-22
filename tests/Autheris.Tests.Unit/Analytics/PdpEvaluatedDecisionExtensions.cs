namespace Autheris.Tests.Unit.Analytics;

using Autheris.Domain.Interfaces;

/// <summary>MISC-06: the aggregation service trusts a pre-evaluated decision only with PDP provenance; tests that inject one mark it explicitly.</summary>
internal static class PdpEvaluatedDecisionExtensions
{
    public static TableAccessDecision AsPdpEvaluated(this TableAccessDecision decision) => decision with { EvaluatedByPdp = true };
}
