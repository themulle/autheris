namespace Autheris.Application.Security;

using System;

/// <summary>
/// SR-H-03: Datasource credentials live exclusively in the server-controlled "datasource:" vault namespace. A
/// client-supplied secret reference is never used verbatim as a vault key outside of this namespace, so a datasource
/// registration can neither overwrite configured secrets (e.g. forwardauth-secret) nor read arbitrary
/// configuration keys, environment variables or mounted files.
/// </summary>
public static class DatasourceSecretNamespace
{
    public const string Prefix = "datasource:";

    private const int MaxRefLength = 256;

    public static bool IsNamespaced(string? secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef) ||
            secretRef.Length > MaxRefLength ||
            secretRef.Length <= Prefix.Length ||
            !secretRef.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in secretRef)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds a server-generated reference such as <c>datasource:{tenant}:{domain}:{name}:secret</c>.</summary>
    public static string Build(params string[] segments)
    {
        var parts = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            parts[i] = Sanitize(segments[i]);
        }

        return Prefix + string.Join(':', parts);
    }

    /// <summary>Fail-closed guard for client-supplied references that point at an existing datasource secret.</summary>
    public static string RequireNamespaced(string secretRef)
    {
        if (!IsNamespaced(secretRef))
        {
            throw new ArgumentException(
                $"Auth.SecretRef must reference the '{Prefix}' namespace; arbitrary secret references are not accepted.",
                nameof(secretRef));
        }

        return secretRef;
    }

    private static string Sanitize(string? segment)
    {
        var chars = (segment ?? string.Empty).ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(chars[i]) || chars[i] is '-' or '_' or '.'))
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
