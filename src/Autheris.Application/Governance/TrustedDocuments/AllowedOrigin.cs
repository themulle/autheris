namespace Autheris.Application.Governance.TrustedDocuments;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Normal form of a browser Origin (Issue #281, SEC-281-3): <c>scheme://host[:port]</c>, lower case, default port elided. The same function
/// normalises registered origins and the request header, so the comparison is an ordinal equality of normal forms.
/// </summary>
public static class AllowedOrigin
{
    public const int MaxInputLength = 256;

    public static bool TryNormalize(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaxInputLength || value.Any(c => c <= ' ' || c == '\\' || c > '~'))
        {
            return false;
        }

        if (value.Contains('?', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if ((uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length != 0 || uri.Host.Length == 0 || uri.AbsolutePath != "/")
        {
            return false;
        }

        // Only the bare origin (optionally with one trailing slash) is a valid input; a deeper path never gets here via AbsolutePath.
        string host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.ToLowerInvariant() : uri.IdnHost.ToLowerInvariant();
        normalized = uri.IsDefaultPort
            ? $"{uri.Scheme}://{host}"
            : $"{uri.Scheme}://{host}:{uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return true;
    }
}
