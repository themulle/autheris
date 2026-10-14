namespace Autheris.Application.Security;

using System;
using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Builds an outbound target URI from a trusted base URL and an untrusted relative path
/// (SR-M-14, SR-M-15). Fails closed: any input that could re-target the request to another
/// scheme, host or port (backslashes, schemes, network-path references, userinfo, control characters)
/// is rejected, and the combined URI is verified against the base authority.
/// </summary>
public static class OutboundPathPolicy
{
    /// <summary>
    /// Tries to combine <paramref name="baseUri"/> and <paramref name="path"/>.
    /// </summary>
    /// <param name="baseUri">Trusted absolute http(s) base URL.</param>
    /// <param name="path">Untrusted relative path (optionally with query).</param>
    /// <param name="preserveBasePath">
    /// When true the path is resolved relative to the base path (leading slashes are stripped);
    /// when false a leading slash is root-relative.
    /// </param>
    /// <param name="allowDotSegments">Whether ".." sequences are tolerated.</param>
    /// <param name="target">The combined URI on success.</param>
    /// <param name="error">A short, non-sensitive reason on failure.</param>
    public static bool TryBuild(
        Uri baseUri,
        string? path,
        bool preserveBasePath,
        bool allowDotSegments,
        [NotNullWhen(true)] out Uri? target,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        target = null;
        error = null;

        if (!baseUri.IsAbsoluteUri)
        {
            error = "Base URL must be absolute.";
            return false;
        }

        path ??= string.Empty;

        foreach (var c in path)
        {
            if (char.IsControl(c) || c == '\\' || c == '@')
            {
                error = "Path contains forbidden characters.";
                return false;
            }
        }

        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            error = "Network-path references are not allowed.";
            return false;
        }

        if (!allowDotSegments && path.Contains("..", StringComparison.Ordinal))
        {
            error = "Path traversal sequences are not allowed.";
            return false;
        }

        var trimmed = path.TrimStart('/');

        // A colon inside the first segment makes the reference scheme-qualified ("https:...", "x:...").
        var firstSegmentEnd = trimmed.AsSpan().IndexOfAny('/', '?', '#');
        var firstSegment = firstSegmentEnd < 0 ? trimmed.AsSpan() : trimmed.AsSpan(0, firstSegmentEnd);
        if (firstSegment.Contains(':'))
        {
            error = "Scheme-qualified paths are not allowed.";
            return false;
        }

        var candidate = preserveBasePath ? trimmed : (path.Length > 0 && path[0] == '/' ? path : trimmed);

        Uri combined;
        try
        {
            combined = new Uri(baseUri, candidate);
        }
        catch (UriFormatException)
        {
            error = "Could not construct a valid URI.";
            return false;
        }

        if (!string.Equals(combined.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(combined.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            combined.Port != baseUri.Port ||
            !string.IsNullOrEmpty(combined.UserInfo))
        {
            error = "Combined URI does not match the base authority.";
            return false;
        }

        target = combined;
        return true;
    }
}
