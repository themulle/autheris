namespace Autheris.Application.Federation.Services;

using System;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// SR-M-18: canonical, unambiguous signed-material format for the forwarded Zero-Trust context (version "v2").
/// Every field is length-prefixed (UTF-8 byte count) so field boundaries cannot be shifted by ':' characters in
/// values, and the SHA-256 of the request body is part of the material so the signature binds the request payload.
/// </summary>
public static class FederationContextSignature
{
    public const string CurrentVersion = "v2";
    public const string VersionHeader = "X-Autheris-Signature-Version";
    public const string BodyHashHeader = "X-Autheris-Body-Sha256";

    /// <summary>Lower-case hex SHA-256 of the body (of the empty body when <paramref name="body"/> is empty).</summary>
    public static string HashBody(ReadOnlySpan<byte> body) => Convert.ToHexStringLower(SHA256.HashData(body));

    public static string BuildCanonicalV2(
        string tenant,
        string? userSid,
        string timestamp,
        string nonce,
        string subgraph,
        string? roles,
        string? method,
        string? path,
        string? bodySha256)
    {
        var sb = new StringBuilder(256);
        Append(sb, CurrentVersion);
        Append(sb, tenant);
        Append(sb, userSid);
        Append(sb, timestamp);
        Append(sb, nonce);
        Append(sb, subgraph);
        Append(sb, roles);
        Append(sb, method);
        Append(sb, path);
        Append(sb, bodySha256);
        return sb.ToString();
    }

    /// <summary>Legacy (pre-v2) ':'-joined form; only for the opt-in rolling-upgrade verifier path.</summary>
    public static string BuildLegacy(string tenant, string? userSid, string timestamp, string nonce, string subgraph, string? roles, string? method, string? path)
        => $"{tenant}:{userSid ?? string.Empty}:{timestamp}:{nonce}:{subgraph}:{roles ?? string.Empty}:{method ?? string.Empty}:{path ?? string.Empty}";

    public static string Sign(string canonical, string signingKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void Append(StringBuilder sb, string? value)
    {
        var v = value ?? string.Empty;
        sb.Append(Encoding.UTF8.GetByteCount(v)).Append(':').Append(v).Append(';');
    }
}
