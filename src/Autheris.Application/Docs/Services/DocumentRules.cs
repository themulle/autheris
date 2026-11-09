namespace Autheris.Application.Docs.Services;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Identifier validation rules, reserved names, and deterministic key generation for documentation.
/// </summary>
public static partial class DocumentRules
{
    public const int MaxAppIdLength = 64;
    public const int MaxDocIdLength = 128;
    public const int MaxTenantIdLength = 128;
    public const int MaxErrorCodeLength = 64;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,127}$")]
    public static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^[A-Z0-9_-]{1,64}$")]
    public static partial Regex ErrorCodePattern();

    [GeneratedRegex(@"[^a-z0-9\-]+")]
    private static partial Regex SlugStripRegex();

    public static readonly IReadOnlySet<string> ReservedAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "autheris",
        "global",
        "system"
    };

    public static bool IsReservedAppId(string? appId) =>
        !string.IsNullOrWhiteSpace(appId) && ReservedAppIds.Contains(appId.Trim());

    public static bool IsValidIdentifier(string? identifier, int maxLength = 128)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        var trimmed = identifier.Trim();
        if (trimmed.Length > maxLength || trimmed.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return IdentifierPattern().IsMatch(trimmed.ToLowerInvariant());
    }

    public static bool IsValidAppId(string? appId) =>
        IsValidIdentifier(appId, MaxAppIdLength);

    public static bool IsValidDocId(string? docId) =>
        IsValidIdentifier(docId, MaxDocIdLength);

    public static bool IsValidTenantId(string? tenantId) =>
        IsValidIdentifier(tenantId, MaxTenantIdLength);

    public static bool IsValidErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return false;
        }

        return ErrorCodePattern().IsMatch(errorCode.Trim());
    }

    public static string GenerateDocId(string title, string sanitizedContent)
    {
        ArgumentNullException.ThrowIfNull(sanitizedContent);

        var slug = Slugify(title);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sanitizedContent));
        var hex = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();

        const int maxSlugLength = MaxDocIdLength - 1 - 8; // 119
        if (slug.Length > maxSlugLength)
        {
            slug = slug[..maxSlugLength].TrimEnd('-');
            if (string.IsNullOrEmpty(slug))
            {
                slug = "doc";
            }
        }

        return $"{slug}-{hex}";
    }

    public static string Slugify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "untitled";
        }

        var slug = SlugStripRegex().Replace(text.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "untitled" : slug;
    }
}
