namespace Autheris.Application.Security;

using System;
using System.Security;
using System.Text;
using Autheris.Application.Common.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// SEC EX-16 / K-X09: Central resolver for secret references across all integration clients.
/// Resolves secret references via <see cref="IKeyVaultSecretProvider"/> without leaking secret values or references
/// into logs or exception messages. Fails closed (throws <see cref="SecurityException"/>) outside Development.
/// </summary>
public static class SecretReferenceResolver
{
    /// <summary>
    /// Returns true when <paramref name="referenceOrValue"/> matches a supported secret reference syntax
    /// (e.g. kv://, vault://, vault:, keyvault:, azure-keyvault://, file:, datasource:, or Azure Key Vault HTTPS URL).
    /// Returns false for empty/null strings and plaintext/inline secret values.
    /// </summary>
    public static bool IsReference(string? referenceOrValue)
    {
        if (string.IsNullOrWhiteSpace(referenceOrValue))
        {
            return false;
        }

        var trimmed = referenceOrValue.Trim();

        if (trimmed.StartsWith("kv://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("vault://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("vault:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("keyvault:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("azure-keyvault://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("datasource:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            (uri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Host, "vault.azure.net", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    public static string? Resolve(
        IKeyVaultSecretProvider? secretProvider,
        string? referenceOrValue,
        IHostEnvironment? environment,
        bool allowPlaintextInDevelopment = true,
        ILogger? logger = null,
        string? secretDescription = "secret")
    {
        if (string.IsNullOrWhiteSpace(referenceOrValue))
        {
            return null;
        }

        // Review G5: a missing host environment is treated as production (fail closed), never as Development.
        var isDev = environment != null &&
                    string.Equals(environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);

        if (secretProvider != null)
        {
            try
            {
                var secretBytes = secretProvider.GetSecretBytes(referenceOrValue);
                if (secretBytes.Length > 0)
                {
                    var resolved = Encoding.UTF8.GetString(secretBytes);
                    if (!string.IsNullOrWhiteSpace(resolved) &&
                        (!string.Equals(resolved, referenceOrValue, StringComparison.Ordinal) || (allowPlaintextInDevelopment && isDev)))
                    {
                        return resolved;
                    }
                }
            }
            catch (Exception ex)
            {
                // SEC EX-16 / E-08: Never log ex.Message or referenceOrValue, as it could contain the secret string.
                logger?.LogWarning("Secret lookup failed for {SecretDescription} ({ExceptionType}).", secretDescription, ex.GetType().Name);
                if (!isDev)
                {
                    throw new SecurityException(
                        $"Failed to resolve {secretDescription} reference in non-development environment.");
                }
            }
        }

        if (allowPlaintextInDevelopment && isDev)
        {
            logger?.LogWarning("Using configured {SecretDescription} as plaintext fallback (Development only).", secretDescription);
            return referenceOrValue;
        }

        throw new SecurityException(
            $"The {secretDescription} reference could not be resolved to a non-empty secret (fail-closed).");
    }
}
