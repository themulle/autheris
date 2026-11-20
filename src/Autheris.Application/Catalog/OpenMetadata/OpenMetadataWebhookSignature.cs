namespace Autheris.Application.Catalog.OpenMetadata;

using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

/// <summary>
/// HMAC-SHA256 verification of OpenMetadata webhook payloads. Stateless; the digest is compared in constant time and the
/// secret, the signature and the payload are never logged.
/// </summary>
public static class OpenMetadataWebhookSignature
{
    private const string Sha256Prefix = "sha256=";
    private const int HexLength = 64;

    /// <summary>
    /// Verifies <paramref name="signatureHeader"/> against the HMAC-SHA256 of <paramref name="payload"/> (optional
    /// <c>sha256=</c> prefix). A blank secret, a non-hex value, a wrong length or any exception yields <c>false</c>.
    /// <paramref name="bypass"/> is the explicit dangerous-configuration switch and the only way to accept an unverified payload.
    /// </summary>
    public static bool Verify(string payload, string? signatureHeader, string? secret, bool bypass, ILogger? logger = null)
    {
        if (bypass)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        try
        {
            var clean = signatureHeader.Trim();
            if (clean.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
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

            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(clean));
        }
        catch (Exception ex)
        {
            logger?.LogWarning("OpenMetadata webhook signature verification failed due to {ExceptionType}.", ex.GetType().Name);
            return false;
        }
    }
}
