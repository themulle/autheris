using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Autheris.Tests.Simulation.Infrastructure;

/// <summary>One finding. <see cref="ErrorClass"/> is the dedupe key (see <see cref="SimErrorClass.Build"/>).</summary>
public sealed record SimFinding(FindingKind Kind, string ErrorClass, string MinimalInput, string Expected, string Actual, string Repro)
{
    /// <summary>Stable 12-hex id derived from the error class.</summary>
    public string Id => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ErrorClass)))[..12];
}

public static partial class SimErrorClass
{
    private const int MaxLength = 120;

    [GeneratedRegex("[0-9]+")]
    private static partial Regex Digits();

    [GeneratedRegex("'[^']*'|\"[^\"]*\"")]
    private static partial Regex Quoted();

    /// <summary>kind + status or exception type + message normalised (digits to <c>#</c>, quoted text to <c>'?'</c>, trimmed to 120 chars).</summary>
    public static string Build(FindingKind kind, string statusOrExceptionType, string? message)
    {
        var normalised = Digits().Replace(Quoted().Replace(message ?? string.Empty, "'?'"), "#").Trim();
        var full = $"{kind}:{statusOrExceptionType}:{normalised}";
        return full.Length <= MaxLength ? full : full[..MaxLength];
    }
}
