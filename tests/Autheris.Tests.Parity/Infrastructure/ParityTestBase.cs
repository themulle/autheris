namespace Autheris.Tests.Parity.Infrastructure;

/// <summary>Base class of every parity test class (D-2); the constructor enforces the run guard.</summary>
public abstract class ParityTestBase
{
    protected ParityTestBase() => ParityRunGuard.EnsureEnabled();
}
