namespace Autheris.Application.Governance.TrustedDocuments;

using System.Security.Cryptography;

/// <summary>
/// Exact-text document hash of the app-scoped path (Issue #281, owner decision 3, SEC-281-1): SHA-256 over the UTF-8 bytes of the
/// request's source text without any normalisation, rendered as 64 lower-case hex characters. Whitespace changes the hash.
/// </summary>
public static class TrustedDocumentHash
{
    public const int HexLength = 64;

    public static string ComputeExact(ReadOnlySpan<byte> utf8) => Convert.ToHexStringLower(SHA256.HashData(utf8));

    /// <summary>Hashes the UTF-8 encoding of <paramref name="text"/> (what the parser later consumes).</summary>
    public static string ComputeExact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ComputeExact(System.Text.Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Accepts 64 hex characters in either case and returns them lower-cased.</summary>
    public static bool TryNormalize(string? value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (value is not { Length: HexLength })
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        normalized = value.ToLowerInvariant();
        return true;
    }
}
