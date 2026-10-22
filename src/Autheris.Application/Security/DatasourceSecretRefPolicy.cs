namespace Autheris.Application.Security;

using System;

/// <summary>
/// SR-H-03: Namespace policy for secret references of API-registered datasources. A client-supplied
/// <c>Auth.SecretRef</c> is never used verbatim as vault key: it may only point into the caller's own
/// <c>datasource:{domain}:</c> namespace. Everything else (well-known secrets, configuration keys, environment
/// variables, <c>file:</c> references) is reserved for operator configuration and refused fail-closed.
/// </summary>
public static class DatasourceSecretRefPolicy
{
    public const string Namespace = "datasource:";

    /// <summary>True when the reference lies inside the server-managed datasource namespace.</summary>
    public static bool IsInDatasourceNamespace(string? secretRef) =>
        !string.IsNullOrWhiteSpace(secretRef) &&
        secretRef.Length > Namespace.Length &&
        secretRef.StartsWith(Namespace, StringComparison.OrdinalIgnoreCase) &&
        !HasControlOrTraversal(secretRef);

    /// <summary>Builds the namespace prefix <c>datasource:{domain}:</c> for a domain.</summary>
    public static string DomainPrefix(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        if (domain.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Datasource domain must not contain ':'.", nameof(domain));
        }

        return $"{Namespace}{domain}:";
    }

    private static readonly string[] OperatorConfigRoots =
    [
        "Gateway:",
        "ConnectionStrings:",
        "Authentication:"
    ];

    /// <summary>
    /// Validates a client-supplied reference against <paramref name="requiredPrefix"/> (e.g. from
    /// <see cref="DomainPrefix"/>). Null/empty means "server generates"; anything else outside the prefix is rejected.
    /// Rejects any client reference containing known operator configuration roots in its remainder.
    /// </summary>
    public static void ValidateClientRef(string? secretRef, string requiredPrefix)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            return;
        }

        if (!secretRef.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase) ||
            secretRef.Length == requiredPrefix.Length ||
            HasControlOrTraversal(secretRef))
        {
            throw new ArgumentException("Auth.SecretRef must be empty or inside the datasource's own secret namespace.", nameof(secretRef));
        }

        var remainder = secretRef[requiredPrefix.Length..];
        foreach (var root in OperatorConfigRoots)
        {
            if (remainder.Contains(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Auth.SecretRef must not contain operator configuration root keys.", nameof(secretRef));
            }
        }
    }

    private static bool HasControlOrTraversal(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return value.Contains("..", StringComparison.Ordinal);
    }
}
