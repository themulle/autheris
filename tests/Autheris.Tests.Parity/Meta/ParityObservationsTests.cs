using Autheris.Tests.Parity.Infrastructure;
using Shouldly;

namespace Autheris.Tests.Parity.Meta;

[Trait("Category", "Parity")]
public sealed class ParityObservationsTests : ParityTestBase
{
    [Fact]
    public void ParityObservations_HolderIsClearedWhenTheCellEnds()
    {
        ParityObservations.IsActive.ShouldBeFalse();
        using (var holder = ParityObservations.Begin())
        {
            ParityObservations.IsActive.ShouldBeTrue();
            ParityObservations.RecordTolerated501("SQL_FUNCTION_UNSUPPORTED_DIALECT");
            holder.Tolerated501.ShouldBe("SQL_FUNCTION_UNSUPPORTED_DIALECT");
        }

        ParityObservations.IsActive.ShouldBeFalse();
        ParityObservations.RecordTolerated501("LATE");
    }

    [Fact]
    public void ParityObservations_DisposeKeepsANewerHolder()
    {
        var first = ParityObservations.Begin();
        using var second = ParityObservations.Begin();
        first.Dispose();
        ParityObservations.IsActive.ShouldBeTrue();
        ParityObservations.RecordTolerated501("X");
        second.Tolerated501.ShouldBe("X");
    }
}
