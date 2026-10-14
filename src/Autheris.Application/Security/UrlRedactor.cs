namespace Autheris.Application.Security;

using System;
using System.Globalization;

/// <summary>
/// SR-L-14: Reduces a URL to <c>scheme://host[:port]/</c> for logs and diagnostics. Userinfo, path, query and
/// fragment (which may carry tokens or secrets) are never emitted.
/// </summary>
public static class UrlRedactor
{
    /// <summary>Placeholder for URLs that cannot be parsed.</summary>
    public const string Invalid = "[invalid-url]";

    /// <summary>Redacts a URI.</summary>
    public static string Redact(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return Invalid;
        }

        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        return $"{uri.Scheme}://{uri.Host}{port}/";
    }

    /// <summary>Redacts a URL string; unparsable input yields <see cref="Invalid"/> (never echoed).</summary>
    public static string Redact(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Redact(uri) : Invalid;
}
