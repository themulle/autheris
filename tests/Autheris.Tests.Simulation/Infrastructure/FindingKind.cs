namespace Autheris.Tests.Simulation.Infrastructure;

public enum FindingKind
{
    WrongResult,
    UnexpectedRejection,
    SchemaNonConformance,
    Crash,
    Hang,
    ServerError,
    PolicyLeak,
}

public static class FindingKinds
{
    /// <summary>SEC-SIM-17: these kinds can never be KnownGaps; the only path is an owner-approved quarantine entry.</summary>
    public static bool IsSecurityClass(this FindingKind kind) =>
        kind is FindingKind.Crash or FindingKind.Hang or FindingKind.ServerError or FindingKind.PolicyLeak;
}
