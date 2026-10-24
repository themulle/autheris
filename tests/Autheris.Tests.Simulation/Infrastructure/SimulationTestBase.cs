namespace Autheris.Tests.Simulation.Infrastructure;

/// <summary>
/// Base class of every guarded simulation test class (all classes outside the <c>Meta</c> namespace); the constructor enforces the run guard.
/// Pure unit tests live in <c>Autheris.Tests.Simulation.Meta</c> and do not derive from it.
/// </summary>
public abstract class SimulationTestBase
{
    protected SimulationTestBase() => SimRunGuard.EnsureEnabled();
}
