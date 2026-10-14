namespace Autheris.Application.Webhooks;

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// SR-L-13: Shared HMAC-SHA256 signing and verification for outbound webhooks (D1).
/// The signed message is <c>"{timestamp}.{body}"</c>; the timestamp is sent in <c>X-Autheris-Timestamp</c> so that
/// receivers can reject replays outside <see cref="DefaultTolerance"/>. Reused by every webhook dispatcher.
/// </summary>
public static class WebhookSignature
{
    /// <summary>Header carrying the timestamped signature (<c>sha256=&lt;hex&gt;</c>).</summary>
    public const string SignatureHeader = "X-Autheris-Signature-256";

    /// <summary>Header carrying the ISO-8601 (round-trip) timestamp covered by the signature.</summary>
    public const string TimestampHeader = "X-Autheris-Timestamp";

    /// <summary>Legacy body-only signature header (deprecated, see <c>Gateway:Webhooks:EmitLegacySignature</c>).</summary>
    public const string LegacySignatureHeader = "X-Autheris-Signature";

    /// <summary>Signature value prefix.</summary>
    public const string Prefix = "sha256=";

    /// <summary>Maximum accepted clock skew / age of a signed delivery.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Formats a timestamp for the timestamp header.</summary>
    public static string FormatTimestamp(DateTimeOffset timestamp) => timestamp.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Computes the lowercase hex HMAC-SHA256 of <paramref name="message"/>.</summary>
    public static string ComputeHex(string secret, string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(message);

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Signs <c>"{timestamp}.{payload}"</c> and returns lowercase hex (without prefix).</summary>
    public static string Sign(string secret, string timestamp, string payload)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(payload);
        return ComputeHex(secret, $"{timestamp}.{payload}");
    }

    /// <summary>Returns the full header value <c>sha256=&lt;hex&gt;</c>.</summary>
    public static string SignHeaderValue(string secret, string timestamp, string payload) =>
        Prefix + Sign(secret, timestamp, payload);

    /// <summary>
    /// Verifies a delivery: timestamp must parse and lie within <paramref name="tolerance"/> (default 5 minutes) of
    /// <paramref name="now"/>, and the signature must match (constant-time comparison). Never throws.
    /// </summary>
    public static bool TryVerify(
        string? secret,
        string? timestampHeader,
        string? payload,
        string? signatureHeader,
        DateTimeOffset now,
        TimeSpan? tolerance = null)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(timestampHeader) || payload is null || string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(timestampHeader, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
        {
            return false;
        }

        if ((now - ts).Duration() > (tolerance ?? DefaultTolerance))
        {
            return false;
        }

        if (!signatureHeader.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(secret, timestampHeader, payload));
        var actual = Encoding.ASCII.GetBytes(signatureHeader[Prefix.Length..]);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
