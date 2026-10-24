using Autheris.Tests.Simulation.Infrastructure;

namespace Autheris.Tests.Simulation.Corpus;

/// <summary>
/// Registered corpus gaps (strict xfail, REQ-SIM-04). Each id has an issue draft under <c>KnownGaps/drafts/&lt;id&gt;.md</c>. Only
/// <c>WrongResult</c> and <c>UnexpectedRejection</c> classes can be registered; the security classes never can (SEC-SIM-17).
/// </summary>
public static class SltKnownGaps
{
    public static SimKnownGaps Create()
    {
        var gaps = new SimKnownGaps();
        return gaps;
    }
}
