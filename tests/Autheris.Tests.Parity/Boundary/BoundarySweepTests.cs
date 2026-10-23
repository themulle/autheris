using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Invariant;
using Shouldly;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Boundary;

/// <summary>
/// Blocks C and D against the in-process host: every boundary value on every filter operand, sort key and paging parameter, per channel.
/// Each case asserts the no-5xx/no-leak invariant (hard failure, never a gap) and the catalogue expectation (oracle result or stable 4xx).
/// </summary>
[Trait("Category", "Parity")]
[Collection(ParityHostCollection.Name)]
public sealed class BoundarySweepTests(ParityHostFixture host) : ParityTestBase
{
    public static IEnumerable<object[]> TypedCases() => BoundaryCases.ChannelNames.SelectMany(ch => BoundaryCases.Rows(ch, BoundaryCases.For));

    public static IEnumerable<object[]> CrossTypeCases() => BoundaryCases.ChannelNames.SelectMany(ch => BoundaryCases.Rows(ch, BoundaryCases.CrossType));

    public static IEnumerable<object[]> ObservedServerErrorCases()
    {
        var rows = BoundaryCases.ChannelNames.SelectMany(ch => BoundaryCases.Rows(ch, BoundaryCases.ObservedServerErrors)).ToList();
        // xunit v2 fails a theory without data; the sentinel row passes while no server error is observed.
        return rows.Count > 0 ? rows : [[NoneSentinel, NoneSentinel]];
    }

    private const string NoneSentinel = "none";

    [ParityTheory]
    [MemberData(nameof(TypedCases))]
    public Task Boundary_Case(string channel, string caseId) => RunAsync("boundary", channel, caseId, assertExpectation: true);

    [ParityTheory]
    [MemberData(nameof(CrossTypeCases))]
    public Task Invariant_NoServerError(string channel, string caseId) => RunAsync("invariant", channel, caseId, assertExpectation: false);

    /// <summary>
    /// Cases that currently return a 5xx or leak internals (SEC-PAR-05). They are kept out of the sweeps above and stay failing
    /// until production is fixed; the inputs and observed statuses are documented in section 5 of the plan.
    /// </summary>
    [ParityTheory]
    [MemberData(nameof(ObservedServerErrorCases))]
    public Task Invariant_ObservedServerErrors(string channel, string caseId) => RunAsync("observed5xx", channel, caseId, assertExpectation: false);

    private async Task RunAsync(string block, string channel, string caseId, bool assertExpectation)
    {
        if (channel == NoneSentinel)
        {
            return;
        }

        var c = BoundaryCases.Find(channel, caseId);
        var classification = BoundaryKnownGaps.Classify(channel, caseId);
        var gap = classification.Kind == BoundaryClass.Gap ? classification.GapId : null;
        var info = new ParityCellInfo(block, channel, c.Slot.ToString().ToLowerInvariant(), caseId, GapId: gap);
        await ParityCell.RunAsync(info, async () =>
        {
            var (result, submitted) = await BoundaryChannels.ExecuteAsync(host, channel, c, CancellationToken.None);
            if (result.NotExpressible)
            {
                throw new PendingCellException(channel, c.Slot.ToString());
            }

            ParityAssert.NoServerError(result, submitted);
            if (block == "observed5xx")
            {
                throw new XunitException(
                    $"Observed server error fixed ({channel}/{caseId}, HTTP {result.Status}): move the case back into the normal sweep (remove it from BoundaryKnownGapData) so the oracle checks it.");
            }

            ParityAssert.StableCode(result);
            if (assertExpectation)
            {
                Check(c, result, BoundaryOracle.Expected(channel, c));
            }
        });
    }

    private static void Check(BoundaryCase c, ChannelResult r, BoundaryExpectation e)
    {
        var failed = ParityAssert.IsError(r);
        switch (e.Kind)
        {
            case Expectation.Ok:
                if (failed || r.Status != 200)
                {
                    throw new XunitException($"Expected success, got HTTP {r.Status} code '{r.Code}': {Snippet(r.RawBody)}");
                }

                if (e.Ids is not null)
                {
                    var actual = BoundaryChannels.Ids(r);
                    (e.Ordered ? actual : actual.Order().ToList()).ShouldBe(e.Ids, $"rows differ: {Snippet(r.RawBody)}");
                }

                break;
            case Expectation.Rejected:
                if (!failed)
                {
                    if (c.Slot == BoundarySlot.Raw && c.RawId!.StartsWith("dml_", StringComparison.Ordinal))
                    {
                        // SEC-PAR-05c: a denied statement must never succeed; never a gap.
                        throw new DeniedRequestSucceededException($"{c.RawId} was accepted with HTTP {r.Status}.");
                    }

                    throw new XunitException($"Expected a stable 4xx rejection, got HTTP {r.Status}: {Snippet(r.RawBody)}");
                }

                break;
            default:
                if (!failed && e.Ids is not null)
                {
                    BoundaryChannels.Ids(r).ShouldBe(e.Ids);
                }

                break;
        }
    }

    private static string Snippet(string body) => body.Length <= 240 ? body : body[..240] + "...";
}
