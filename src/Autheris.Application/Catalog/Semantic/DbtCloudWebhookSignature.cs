namespace Autheris.Application.Catalog.Semantic;

using System;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// HMAC-SHA256 verification of dbt Cloud webhook payloads. Stateless; the digest is compared in constant time.
/// </summary>
public static class DbtCloudWebhookSignature
{
    private const string Sha256Prefix = "sha256=";
    private const int HexLength = 64;

    /// <summary>
    /// Verifies <paramref name="signatureHeader"/> against the HMAC-SHA256 of <paramref name="payload"/>.
    /// A blank secret, a non-hex value or a value of the wrong length is rejected. <paramref name="bypass"/> is the
    /// explicit dangerous-configuration switch and is the only way to accept an unverified payload.
    /// </summary>
    public static bool Verify(string payload, string? signatureHeader, string? secret, bool bypass, bool allowSha256Prefix = true)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (bypass)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        var clean = signatureHeader.Trim();
        if (allowSha256Prefix && clean.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[Sha256Prefix.Length..];
        }

        if (clean.Length != HexLength)
        {
            return false;
        }

        foreach (var c in clean)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        var expected = Convert.FromHexString(clean);
        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }
}
