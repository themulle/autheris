namespace Autheris.Tests.Simulation.Infrastructure;

/// <summary>SEC-SIM-21 / REQ-SIM-01: the guarded simulation suite runs only when <c>AUTHERIS_SIM_RUN</c> is exactly <c>1</c> (fail-closed).</summary>
public static class SimRunGuard
{
    public const string EnvVar = "AUTHERIS_SIM_RUN";

    public const string Message = "Simulation suite runs only in simulation-nightly.yml; set AUTHERIS_SIM_RUN=1 to run locally";

    public static void EnsureEnabled() => EnsureEnabled(Environment.GetEnvironmentVariable(EnvVar));

    public static void EnsureEnabled(string? value)
    {
        if (!string.Equals(value, "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(Message);
        }
    }
}
