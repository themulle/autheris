using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autheris.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Security;

public sealed class DefaultEnvironmentSecretProvider : IKeyVaultSecretProvider
{
    private const string ItsmWebhookSecretInstancePrefix = "itsm:webhook-secret:";

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly Microsoft.Extensions.Logging.ILogger<DefaultEnvironmentSecretProvider>? _logger;

    public DefaultEnvironmentSecretProvider(
        IConfiguration configuration,
        IHostEnvironment environment,
        Microsoft.Extensions.Logging.ILogger<DefaultEnvironmentSecretProvider>? logger = null)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public byte[] GetSecretBytes(string secretRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        var candidates = new List<string> { secretRef };

        // 1. If secretRef is a URI (e.g. https://my-vault.vault.azure.net/secrets/itsm-webhook-secret)
        if (Uri.TryCreate(secretRef, UriKind.Absolute, out var uri))
        {
            var segments = uri.Segments
                .Select(s => s.Trim('/'))
                .Where(s => !string.IsNullOrEmpty(s) && !string.Equals(s, "secrets", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (segments.Count > 0)
            {
                var secretName = segments[0];
                candidates.Add(secretName);
                candidates.Add(secretName.Replace("-", "_"));
                candidates.Add(secretName.Replace("-", ":"));
            }
        }

        // 2. Namespaced aliases strictly derived from the requested secretRef
        var cleanRef = secretRef.Replace(":", "__").Replace("-", "_").ToUpperInvariant();
        candidates.Add(cleanRef);
        candidates.Add(secretRef.Replace("-", "_"));
        candidates.Add(secretRef.Replace("-", ":"));

        // 3. Strictly bounded well-known aliases (exact or prefix match only, preventing accidental cross-secret collisions)
        // SEC H-06: Instance-specific ITSM references ("itsm:<name>:<instance>", e.g. itsm:webhook-secret:{instanceId})
        // never fall back to the global well-known secret; otherwise every instance would share the global key.
        var isInstanceSpecificItsmRef = secretRef.StartsWith("itsm:", StringComparison.OrdinalIgnoreCase) && IsInstanceSpecificReference(secretRef);
        if (isInstanceSpecificItsmRef)
        {
            _logger?.LogDebug("Secret reference '{SecretRef}' is instance-specific; no global alias fallback is applied.", secretRef);
        }
        else if (secretRef.StartsWith("itsm:", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(secretRef, "itsm-webhook-secret", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(secretRef, "ITSM_WEBHOOK_SECRET", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("ITSM__WEBHOOK_SECRET");
            candidates.Add("ITSM_WEBHOOK_SECRET");
            candidates.Add("Gateway:Itsm:WebhookSecret");
        }
        else if (secretRef.StartsWith("hmac:", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(secretRef, "hmac-masking-secret", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(secretRef, "HMAC_SECRET", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(secretRef, "HMAC_SECRET_KEY", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("HMAC_SECRET");
            candidates.Add("HMAC_SECRET_KEY");
            candidates.Add("Gateway:DataMasking:HmacSecret");
            candidates.Add("Gateway__DataMasking__HmacSecret");
        }

        foreach (var key in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var secretVal = _configuration[key];
            if (!string.IsNullOrWhiteSpace(secretVal))
            {
                _logger?.LogWarning("Secret reference '{SecretRef}' resolved from configuration key '{CandidateKey}'. In production, ensure sensitive secrets are stored securely in Azure Key Vault or environment variables rather than configuration files.", secretRef, key);
                return Encoding.UTF8.GetBytes(secretVal);
            }

            var envVal = Environment.GetEnvironmentVariable(key.Replace(":", "__").Replace("-", "_"));
            if (!string.IsNullOrWhiteSpace(envVal))
            {
                _logger?.LogDebug("Resolved secret reference '{SecretRef}' using environment variable '{CandidateKey}'.", secretRef, key);
                return Encoding.UTF8.GetBytes(envVal);
            }

            envVal = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(envVal))
            {
                _logger?.LogDebug("Resolved secret reference '{SecretRef}' using direct environment variable '{CandidateKey}'.", secretRef, key);
                return Encoding.UTF8.GetBytes(envVal);
            }
        }

        // SEC H-06: An unresolved instance-specific ITSM secret is "not found" in every environment. The Development
        // placeholder (reference name as key) would be a publicly known HMAC key for that instance.
        if (isInstanceSpecificItsmRef && secretRef.StartsWith(ItsmWebhookSecretInstancePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Sicherheitsfehler: Das instanzspezifische Secret ({DescribeReference(secretRef)}) ist nicht konfiguriert.");
        }

        // In Development, allow using the secret reference itself as dev key
        if (_environment.IsDevelopment())
        {
            return Encoding.UTF8.GetBytes(secretRef);
        }

        // Fail-fast in non-development if secret cannot be resolved from Key Vault
        throw new InvalidOperationException($"Sicherheitsfehler: Das Secret ({DescribeReference(secretRef)}) konnte weder über Azure Key Vault / Konfiguration noch Umgebungsvariablen aufgelöst werden.");
    }

    /// <summary>
    /// SEC EX-16: Exception messages never contain the secret reference itself – a misconfigured value may be a raw
    /// token and exception messages end up in logs. Only the length and a short SHA-256 prefix are reported so that
    /// operators can correlate the reference with their configuration.
    /// </summary>
    private static string DescribeReference(string secretRef)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(secretRef));
        return $"Referenz mit Länge {secretRef.Length}, SHA-256-Präfix {Convert.ToHexStringLower(hash)[..8]}";
    }

    private static bool IsInstanceSpecificReference(string secretRef)
    {
        var segments = secretRef.Split(':');
        return segments.Length >= 3 && segments.All(segment => segment.Length > 0);
    }
}
