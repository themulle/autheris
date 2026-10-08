using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Autheris.Infrastructure.Cache;

/// <summary>
/// SEC H-01: Hardening of the external Redis connection that carries consent decisions, policy epochs,
/// rate-limit buckets and idempotency keys.
/// </summary>
public static class RedisConnectionSecurity
{
    /// <summary>
    /// Applies the password from <see cref="RedisOptions.PasswordSecretRef"/> (if configured) and enforces,
    /// outside Development, that the connection is authenticated. Applies TLS settings (<see cref="RedisOptions.UseTls"/>,
    /// <see cref="RedisOptions.SslHost"/>, certificate pinning) and enforces, outside Development, TLS for
    /// non-loopback endpoints.
    /// </summary>
    public static ConfigurationOptions Apply(
        ConfigurationOptions configuration,
        RedisOptions redisOptions,
        IKeyVaultSecretProvider? secretProvider,
        IHostEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(redisOptions);

        if (!string.IsNullOrWhiteSpace(redisOptions.PasswordSecretRef))
        {
            if (secretProvider == null)
            {
                throw new InvalidOperationException("Security error: Caching.Redis.PasswordSecretRef is set, but no IKeyVaultSecretProvider is registered.");
            }

            configuration.Password = Encoding.UTF8.GetString(secretProvider.GetSecretBytes(redisOptions.PasswordSecretRef));
        }

        var isDevelopment = environment != null &&
                            (environment.IsDevelopment());

        if (!isDevelopment && string.IsNullOrEmpty(configuration.Password))
        {
            throw new InvalidOperationException(
                "Security error: Redis without authentication is not allowed outside Development. Set Caching.Redis.PasswordSecretRef or a password in the connection string.");
        }

        // SEC H-01: transport encryption
        if (redisOptions.UseTls)
        {
            configuration.Ssl = true;
        }

        if (!string.IsNullOrWhiteSpace(redisOptions.SslHost))
        {
            configuration.SslHost = redisOptions.SslHost;
        }

        ApplyCertificatePinning(configuration, redisOptions.AllowedServerCertificateThumbprints, "Caching.Redis.AllowedServerCertificateThumbprints");

        if (!isDevelopment && !configuration.Ssl && HasNonLoopbackEndpoint(configuration))
        {
            throw new InvalidOperationException(
                "Security error: Redis connections to non-loopback hosts require TLS outside Development. Set Caching.Redis.UseTls = true (or ssl=true in the connection string).");
        }

        return configuration;
    }

    /// <summary>
    /// SEC H-01: Client-side TLS for the embedded Garnet server (only if <see cref="GarnetOptions.EnableTls"/>).
    /// </summary>
    public static ConfigurationOptions ApplyGarnetClientTls(ConfigurationOptions configuration, GarnetOptions garnetOptions)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(garnetOptions);

        if (!garnetOptions.EnableTls)
        {
            return configuration;
        }

        configuration.Ssl = true;
        configuration.SslHost = string.IsNullOrWhiteSpace(garnetOptions.TlsSslHost) ? garnetOptions.Host : garnetOptions.TlsSslHost;
        ApplyCertificatePinning(configuration, garnetOptions.TlsAllowedServerCertificateThumbprints, "Caching.Garnet.TlsAllowedServerCertificateThumbprints");
        return configuration;
    }

    /// <summary>
    /// SEC H-01: Normalizes certificate thumbprints (hex, separators removed, upper case). Invalid entries cause an error.
    /// </summary>
    public static IReadOnlyList<string> NormalizeThumbprints(IEnumerable<string>? thumbprints, string settingName)
    {
        var result = new List<string>();
        if (thumbprints == null)
        {
            return result;
        }

        foreach (var raw in thumbprints)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var builder = new StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                if (c != ':' && c != '-' && !char.IsWhiteSpace(c))
                {
                    builder.Append(char.ToUpperInvariant(c));
                }
            }

            var normalized = builder.ToString();

            // SHA-1 = 40 hex chars, SHA-256 = 64 hex chars
            if ((normalized.Length != 40 && normalized.Length != 64) || !IsHex(normalized))
            {
                throw new InvalidOperationException(
                    $"Security error: Invalid certificate fingerprint in {settingName} (expected: SHA-1 or SHA-256 as hex).");
            }

            result.Add(normalized);
        }

        return result;
    }

    /// <summary>
    /// SEC H-01: Certificate pinning callback. A certificate is accepted only if its SHA-256 or SHA-1 thumbprint
    /// is in the pin list (chain errors are tolerated for pinned certificates, e.g. self-signed ones).
    /// </summary>
    public static RemoteCertificateValidationCallback CreatePinnedCertificateValidator(IReadOnlyList<string> normalizedThumbprints)
    {
        ArgumentNullException.ThrowIfNull(normalizedThumbprints);
        var pins = new HashSet<string>(normalizedThumbprints, StringComparer.OrdinalIgnoreCase);
        return (_, certificate, _, _) => IsPinnedCertificate(certificate, pins);
    }

    internal static bool IsPinnedCertificate(X509Certificate? certificate, IReadOnlySet<string> pins)
    {
        if (certificate == null || pins.Count == 0)
        {
            return false;
        }

        return pins.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256)) ||
               pins.Contains(certificate.GetCertHashString()); // SHA-1 thumbprint (legacy pin format)
    }

    /// <summary>
    /// SEC H-01: True if any configured endpoint is not a loopback address/host.
    /// </summary>
    public static bool HasNonLoopbackEndpoint(ConfigurationOptions configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var endPoint in configuration.EndPoints)
        {
            var isLoopback = endPoint switch
            {
                IPEndPoint ip => IPAddress.IsLoopback(ip.Address),
                DnsEndPoint dns => IsLoopbackHost(dns.Host),
                _ => false
            };

            if (!isLoopback)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim().Trim('[', ']');
        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(trimmed, out var ip) && IPAddress.IsLoopback(ip);
    }

    private static void ApplyCertificatePinning(ConfigurationOptions configuration, IEnumerable<string>? thumbprints, string settingName)
    {
        var pins = NormalizeThumbprints(thumbprints, settingName);
        if (pins.Count == 0)
        {
            return;
        }

        if (!configuration.Ssl)
        {
            throw new InvalidOperationException(
                $"Security error: {settingName} is set, but TLS is not enabled for the connection.");
        }

        configuration.CertificateValidation += CreatePinnedCertificateValidator(pins);
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
