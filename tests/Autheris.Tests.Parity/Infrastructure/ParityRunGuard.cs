namespace Autheris.Tests.Parity.Infrastructure;

/// <summary>SEC-PAR-04: the parity suite runs only when <c>AUTHERIS_PARITY_RUN</c> is exactly <c>1</c>.</summary>
public static class ParityRunGuard
{
    public const string EnvVar = "AUTHERIS_PARITY_RUN";

    public const string Message = "Parity suite runs only in parity-nightly.yml; set AUTHERIS_PARITY_RUN=1 to run locally";

    public static void EnsureEnabled() => EnsureEnabled(Environment.GetEnvironmentVariable(EnvVar));

    public static void EnsureEnabled(string? value)
    {
        if (!string.Equals(value, "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(Message);
        }
    }
}
