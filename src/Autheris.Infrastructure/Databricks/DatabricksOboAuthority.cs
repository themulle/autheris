namespace Autheris.Infrastructure.Databricks;

using System;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Validates and normalizes Entra ID OBO authority URLs against the built-in national-cloud allow-list.
/// </summary>
public static class DatabricksOboAuthority
{
    private const string DefaultLoginHost = "login.microsoftonline.com";
    private const string OboTokenPathSuffix = "/oauth2/v2.0/token";

    private static readonly FrozenSet<string> AllowedHosts = FrozenSet.ToFrozenSet(
        [
            "login.microsoftonline.com",
            "login.microsoftonline.us",
            "login.partner.microsoftonline.cn"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ImplicitOnlyTenants = FrozenSet.ToFrozenSet(
        [
            "common",
            "organizations",
            "consumers"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool TryNormalize(
        string? authority,
        string? tenantId,
        [NotNullWhen(true)] out Uri? tokenEndpoint,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            var tenant = string.IsNullOrWhiteSpace(tenantId) ? "common" : tenantId.Trim();
            if (!IsValidTenantSegment(tenant))
            {
                tokenEndpoint = null;
                error = "has an invalid TenantId: must be a single valid tenant identifier without path separators, query, or fragment characters.";
                return false;
            }

            tokenEndpoint = new Uri($"https://{DefaultLoginHost}/{tenant}{OboTokenPathSuffix}", UriKind.Absolute);
            error = string.Empty;
            return true;
        }

        var raw = authority.Trim();
        if (raw.IndexOfAny(['?', '#', '@', '\\', '%']) >= 0 || HasWhitespaceOrControl(raw))
        {
            tokenEndpoint = null;
            error = "must not contain whitespace, percent-encoding, backslashes, userinfo, query parameters, or fragments.";
            return false;
        }

        if (raw.EndsWith("//", StringComparison.Ordinal))
        {
            tokenEndpoint = null;
            error = "must contain exactly one valid tenant path segment.";
            return false;
        }

        var normalizedInput = raw[^1] == '/' ? raw[..^1] : raw;
        if (!Uri.TryCreate(normalizedInput, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString())
        {
            tokenEndpoint = null;
            error = "must be a well-formed absolute HTTPS URL.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            tokenEndpoint = null;
            error = "must use the HTTPS scheme.";
            return false;
        }

        if (!uri.IsDefaultPort)
        {
            tokenEndpoint = null;
            error = "must use the default HTTPS port (443).";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            tokenEndpoint = null;
            error = "must not contain userinfo, query parameters, or fragments.";
            return false;
        }

        if (!AllowedHosts.Contains(uri.Host))
        {
            tokenEndpoint = null;
            error = $"host '{uri.Host}' is not in the Entra login host allow-list.";
            return false;
        }

        // Extract the raw path directly from normalizedInput so dot-segment collapsing or percent-decoding cannot hide traversal.
        var schemeSep = normalizedInput.IndexOf("://", StringComparison.Ordinal);
        var pathStart = schemeSep >= 0 ? normalizedInput.IndexOf("/", schemeSep + 3, StringComparison.Ordinal) : -1;
        if (pathStart < 0)
        {
            tokenEndpoint = null;
            error = "must include a single tenant path segment.";
            return false;
        }

        var rawPath = normalizedInput[pathStart..];
        if (rawPath.Contains("/./", StringComparison.Ordinal) ||
            rawPath.Contains("/../", StringComparison.Ordinal) ||
            rawPath.EndsWith("/.", StringComparison.Ordinal) ||
            rawPath.EndsWith("/..", StringComparison.Ordinal) ||
            !string.Equals(rawPath, uri.AbsolutePath, StringComparison.Ordinal))
        {
            tokenEndpoint = null;
            error = "must contain a normalized single tenant path segment without dot-segments or path rewriting.";
            return false;
        }

        if (rawPath.Length <= 1)
        {
            tokenEndpoint = null;
            error = "must include a single tenant path segment.";
            return false;
        }

        var segments = rawPath[1..].Split('/');
        if (segments.Length != 1 || !IsValidTenantSegment(segments[0]))
        {
            tokenEndpoint = null;
            error = "must contain exactly one valid tenant path segment.";
            return false;
        }

        var tenantSegment = segments[0];
        if (ImplicitOnlyTenants.Contains(tenantSegment))
        {
            tokenEndpoint = null;
            error = $"cannot specify '{tenantSegment}' explicitly; omit Authority or specify a tenant ID or verified domain.";
            return false;
        }

        tokenEndpoint = new Uri($"https://{uri.Host}/{tenantSegment}{OboTokenPathSuffix}", UriKind.Absolute);
        error = string.Empty;
        return true;
    }

    private static bool IsValidTenantSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) ||
            segment is "." or ".." ||
            segment[0] == '.' ||
            segment[^1] == '.' ||
            segment.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in segment)
        {
            bool allowed = (ch is >= 'a' and <= 'z') ||
                           (ch is >= 'A' and <= 'Z') ||
                           (ch is >= '0' and <= '9') ||
                           ch is '-' or '_' or '.';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasWhitespaceOrControl(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }
}
