using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Autheris.Tests.Simulation.Infrastructure;

/// <summary>REQ-SIM-03: the single randomness source. <c>AUTHERIS_SIM_SEED</c> is up to 10 digits (fail-closed), default is the UTC date <c>yyyyMMdd</c>.</summary>
public static partial class SimSeed
{
    public const string EnvVar = "AUTHERIS_SIM_SEED";

    [GeneratedRegex("^[0-9]{1,10}$")]
    private static partial Regex SeedPattern();

    public static long Current => Resolve(Environment.GetEnvironmentVariable(EnvVar), DateTime.UtcNow);

    public static long Resolve(string? value, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(value))
        {
            return long.Parse(utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        if (!SeedPattern().IsMatch(value))
        {
            throw new InvalidOperationException($"{EnvVar} must match ^[0-9]{{1,10}}$.");
        }

        return long.Parse(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Printed first in every report.</summary>
    public static string Describe() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar))
            ? $"seed={Current} (default: UTC date)"
            : $"seed={Current} ({EnvVar})";

    /// <summary>First 8 bytes of SHA-256(<c>seed:id</c>) as a ulong.</summary>
    public static ulong Hash(long seed, string id)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{seed}:{id}")));
        return BitConverter.ToUInt64(digest, 0);
    }
}
