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

    private readonly string _secretsDirectory;

    public DefaultEnvironmentSecretProvider(
        IConfiguration configuration,
        IHostEnvironment environment,
        Microsoft.Extensions.Logging.ILogger<DefaultEnvironmentSecretProvider>? logger = null,
        string? secretsDirectory = null)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
        _secretsDirectory = string.IsNullOrWhiteSpace(secretsDirectory) ? "/run/secrets" : Path.GetFullPath(secretsDirectory);
    }

    public byte[] GetSecretBytes(string secretRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        // SC-07: Support file: reference type for container secrets mounted under /run/secrets/
        if (secretRef.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = secretRef["file:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new InvalidOperationException($"Security error: Empty file path in secret reference ({DescribeReference(secretRef)}).");
            }

            var fullPath = Path.GetFullPath(filePath);
            var allowedDir = _secretsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Path-traversal and allowlist check
            if (!fullPath.StartsWith(allowedDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !string.Equals(fullPath, allowedDir, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Security error: Secret file reference ({DescribeReference(secretRef)}) is outside the allowed directory '{allowedDir}'.");
            }

            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"Security error: Secret file ({DescribeReference(secretRef)}) does not exist.");
            }

            // Symlink check: ensure link target (if any) also stays inside allowlist
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.LinkTarget != null)
            {
                var target = fileInfo.ResolveLinkTarget(true);
                if (target == null || (!target.FullName.StartsWith(allowedDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !string.Equals(target.FullName, allowedDir, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException($"Security error: Secret file reference ({DescribeReference(secretRef)}) is a symlink resolving outside the allowed directory.");
                }
            }

            var fileBytes = File.ReadAllBytes(fullPath);

            // Trim trailing newlines (\r, \n) and trailing spaces
            int length = fileBytes.Length;
            while (length > 0 && (fileBytes[length - 1] == (byte)'\n' || fileBytes[length - 1] == (byte)'\r' || fileBytes[length - 1] == (byte)' '))
            {
                length--;
            }

            if (length < fileBytes.Length)
            {
                fileBytes = fileBytes[..length];
            }

            ValidateSecretLength(fileBytes, secretRef);
            return fileBytes;
        }

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
            _logger?.LogDebug("Secret reference '{SecretRef}' is instance-specific; no global alias fallback is applied.", DescribeReference(secretRef));
        }
        else if (secretRef.StartsWith("itsm:", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(secretRef, "itsm-webhook-secret", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(secretRef, "ITSM_WEBHOOK_SECRET", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("ITSM__WEBHOOK_SECRET");
            candidates.Add("ITSM_WEBHOOK_SECRET");
            candidates.Add("Gateway:Itsm:WebhookSecret");
        }
        // R-DEP-1: well-known aliases apply to exact reference names only. A prefix match ("audit:tenant-x") would
        // silently resolve a missing, specific secret to the global one.
        else if (string.Equals(secretRef, "hmac-masking-secret", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(secretRef, "HMAC_SECRET", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(secretRef, "HMAC_SECRET_KEY", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("HMAC_SECRET");
            candidates.Add("HMAC_SECRET_KEY");
            candidates.Add("Gateway:DataMasking:HmacSecret");
            candidates.Add("Gateway__DataMasking__HmacSecret");
        }
        else if (string.Equals(secretRef, "audit-hmac-key", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(secretRef, "AUDIT_HMAC_KEY", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("AUDIT_HMAC_KEY");
            candidates.Add("Gateway:GovernanceDb:AuditHmacKey");
            candidates.Add("Gateway__GovernanceDb__AuditHmacKey");
        }
        else if (string.Equals(secretRef, "forwardauth-secret", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(secretRef, "FORWARDAUTH_SHARED_SECRET", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("FORWARDAUTH_SHARED_SECRET");
            candidates.Add("Gateway:Authentication:ForwardAuth:SharedSecret");
            candidates.Add("Gateway__Authentication__ForwardAuth__SharedSecret");
        }

        foreach (var key in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var secretVal = _configuration[key];
            if (!string.IsNullOrWhiteSpace(secretVal))
            {
                _logger?.LogWarning("Secret reference '{SecretRef}' resolved from configuration key '{CandidateKey}'. In production, ensure sensitive secrets are stored securely in Azure Key Vault or environment variables rather than configuration files.", SanitizeForLog(DescribeReference(secretRef)), SanitizeForLog(key));
                var bytes = Encoding.UTF8.GetBytes(secretVal);
                ValidateSecretLength(bytes, secretRef);
                return bytes;
            }

            var envVal = Environment.GetEnvironmentVariable(key.Replace(":", "__").Replace("-", "_"));
            if (!string.IsNullOrWhiteSpace(envVal))
            {
                _logger?.LogDebug("Resolved secret reference '{SecretRef}' using environment variable '{CandidateKey}'.", SanitizeForLog(DescribeReference(secretRef)), SanitizeForLog(key));
                var bytes = Encoding.UTF8.GetBytes(envVal);
                ValidateSecretLength(bytes, secretRef);
                return bytes;
            }

            envVal = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(envVal))
            {
                _logger?.LogDebug("Resolved secret reference '{SecretRef}' using direct environment variable '{CandidateKey}'.", SanitizeForLog(DescribeReference(secretRef)), SanitizeForLog(key));
                var bytes = Encoding.UTF8.GetBytes(envVal);
                ValidateSecretLength(bytes, secretRef);
                return bytes;
            }
        }

        // SEC H-06: An unresolved instance-specific ITSM secret is "not found" in every environment. The Development
        // placeholder (reference name as key) would be a publicly known HMAC key for that instance.
        if (isInstanceSpecificItsmRef && secretRef.StartsWith(ItsmWebhookSecretInstancePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Security error: The instance-specific secret ({DescribeReference(secretRef)}) is not configured.");
        }

        // In Development, allow using the secret reference itself as dev key
        if (_environment.IsDevelopment())
        {
            return Encoding.UTF8.GetBytes(secretRef);
        }

        // Fail-fast in non-development if secret cannot be resolved from Key Vault
        throw new InvalidOperationException($"Security error: The secret ({DescribeReference(secretRef)}) could not be resolved via Azure Key Vault / configuration or environment variables.");
    }

    /// <summary>
    /// SEC EX-16: Exception messages never contain the secret reference itself – a misconfigured value may be a raw
    /// token and exception messages end up in logs. Only the length and a short SHA-256 prefix are reported so that
    /// operators can correlate the reference with their configuration.
    /// </summary>
    private static string DescribeReference(string secretRef)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(secretRef));
        return $"Reference with length {secretRef.Length}, SHA-256 prefix {Convert.ToHexStringLower(hash)[..8]}";
    }

    private static bool IsInstanceSpecificReference(string secretRef)
    {
        var segments = secretRef.Split(':');
        return segments.Length >= 3 && segments.All(segment => segment.Length > 0);
    }

    private void ValidateSecretLength(byte[] bytes, string secretRef)
    {
        // DEP-6 / POL-14: Enforce minimum key length of 32 bytes outside Development for cryptographic keys
        if (!_environment.IsDevelopment() && IsCryptographicKeyRequiringMinLength(secretRef) && bytes.Length < 32)
        {
            throw new InvalidOperationException(
                $"Security error: The cryptographic secret ({DescribeReference(secretRef)}) must be at least 32 bytes long outside the development environment (current length: {bytes.Length}).");
        }
    }

    private static bool IsCryptographicKeyRequiringMinLength(string secretRef)
    {
        return secretRef.StartsWith("hmac", StringComparison.OrdinalIgnoreCase) ||
               secretRef.Contains("hmac", StringComparison.OrdinalIgnoreCase) ||
               secretRef.StartsWith("audit", StringComparison.OrdinalIgnoreCase) ||
               secretRef.Contains("audit", StringComparison.OrdinalIgnoreCase) ||
               secretRef.StartsWith("forwardauth", StringComparison.OrdinalIgnoreCase) ||
               secretRef.Contains("forwardauth", StringComparison.OrdinalIgnoreCase) ||
               secretRef.Contains("forward-auth", StringComparison.OrdinalIgnoreCase);
    }
    private static string SanitizeForLog(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}
