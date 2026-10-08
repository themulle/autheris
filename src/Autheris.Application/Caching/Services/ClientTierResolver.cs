namespace Autheris.Application.Caching.Services;

using System;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class ClientTierResolver : IClientTierResolver
{
    private readonly ILogger<ClientTierResolver> _logger;
    private readonly Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>? _gatewayOptions;
    private readonly ConcurrentDictionary<string, (ClientTier Tier, DateTimeOffset Expiry)> _apiKeyCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ClientTier> _registeredKeys = new(StringComparer.Ordinal);

    private const int MaxCacheSize = 10_000;

    public ClientTierResolver(
        ILogger<ClientTierResolver> logger,
        Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>? gatewayOptions = null)
    {
        _logger = logger;
        _gatewayOptions = gatewayOptions;

        var configuredKeys = gatewayOptions?.Value?.RateLimiting?.ClientTiers?.ApiKeys;
        if (configuredKeys != null)
        {
            foreach (var (key, tierName) in configuredKeys)
            {
                if (!string.IsNullOrWhiteSpace(key) && Enum.TryParse<ClientTier>(tierName, true, out var t))
                {
                    _registeredKeys[key.Trim()] = t;
                }
            }
        }
    }

    public void RegisterApiKey(string apiKey, ClientTier tier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _registeredKeys[apiKey.Trim()] = tier;
    }

    public Task<ClientQuotaContext> ResolveAsync(
        ClaimsPrincipal? principal,
        string? apiKey,
        string? clientIp,
        CancellationToken ct = default)
    {
        var tierOptions = _gatewayOptions?.Value?.RateLimiting?.ClientTiers;

        // 1. Check registered API Key
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var cleanKey = apiKey.Trim();
            if (_registeredKeys.TryGetValue(cleanKey, out var registeredTier))
            {
                var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(cleanKey));
                var hexHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                var subjectId = "key_" + hexHash[..16];

                EnsureCacheCapacity();
                _apiKeyCache[subjectId] = (registeredTier, DateTimeOffset.UtcNow.AddMinutes(10));
                return Task.FromResult(new ClientQuotaContext(subjectId, registeredTier, ClientQuotaPolicy.ForTier(registeredTier, tierOptions)));
            }
        }

        // 2. Check JWT Claims or RoleTierMappings from Principal (Authenticated user has precedence over unregistered API keys)
        if (principal?.Identity?.IsAuthenticated == true)
        {
            var tierClaim = principal.FindFirst("tier")?.Value ??
                            principal.FindFirst("client_tier")?.Value ??
                            principal.FindFirst("urn:autheris:tier")?.Value;

            var subjectId = BuildAuthenticatedSubjectId(principal);

            if (!string.IsNullOrWhiteSpace(tierClaim) && Enum.TryParse<ClientTier>(tierClaim, true, out var parsedTier))
            {
                return Task.FromResult(new ClientQuotaContext(subjectId, parsedTier, ClientQuotaPolicy.ForTier(parsedTier, tierOptions)));
            }

            // Check Role-to-Tier mappings
            if (tierOptions?.RoleTierMappings != null && tierOptions.RoleTierMappings.Count > 0)
            {
                ClientTier? highestRoleTier = null;
                foreach (var (role, tierStr) in tierOptions.RoleTierMappings)
                {
                    if (principal.IsInRole(role) && Enum.TryParse<ClientTier>(tierStr, true, out var mappedTier))
                    {
                        if (highestRoleTier == null || mappedTier > highestRoleTier.Value)
                        {
                            highestRoleTier = mappedTier;
                        }
                    }
                }

                if (highestRoleTier.HasValue)
                {
                    return Task.FromResult(new ClientQuotaContext(subjectId, highestRoleTier.Value, ClientQuotaPolicy.ForTier(highestRoleTier.Value, tierOptions)));
                }
            }

            // Default authenticated user to Standard tier if no explicit tier claim or role mapping is present
            return Task.FromResult(new ClientQuotaContext(subjectId, ClientTier.Standard, ClientQuotaPolicy.ForTier(ClientTier.Standard, tierOptions)));
        }

        // 3. SEC M-16: Unbekannte X-API-Key-Werte werden ignoriert. Früher erzeugte jeder zufällige Key einen frischen Bucket;
        //    jetzt fällt der Aufrufer auf den IP-basierten anonymen Bucket zurück.
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug("Unregistered X-API-Key ignored for quota resolution; falling back to anonymous IP bucket.");
        }

        // 4. Fallback: Pure Anonymous / Free tier with IP
        var ip = !string.IsNullOrWhiteSpace(clientIp) ? clientIp : "anonymous";
        var freeSubject = $"anon_{ip}";
        return Task.FromResult(new ClientQuotaContext(freeSubject, ClientTier.Free, ClientQuotaPolicy.ForTier(ClientTier.Free, tierOptions)));
    }

    /// <summary>
    /// SEC M-16: Quota-Subjekt eines angemeldeten Aufrufers = Tenant + Benutzer-SID; die client_id wird nur ergänzt.
    /// Dadurch teilen sich Benutzer derselben Client-Anwendung keinen gemeinsamen Bucket mehr.
    /// </summary>
    internal static string BuildAuthenticatedSubjectId(ClaimsPrincipal principal)
    {
        string tenant;
        try
        {
            tenant = principal.GetTenantId().Value;
        }
        catch (System.Security.SecurityException)
        {
            tenant = "invalid-tenant";
        }

        var userSid = principal.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            userSid = principal.Identity?.Name;
        }

        if (string.IsNullOrWhiteSpace(userSid))
        {
            userSid = "authenticated_user";
        }

        var clientId = principal.FindFirst("client_id")?.Value ??
                       principal.FindFirst("azp")?.Value ??
                       principal.FindFirst("appid")?.Value;

        var subject = $"user:{tenant}:{userSid}";
        if (!string.IsNullOrWhiteSpace(clientId) && !string.Equals(clientId, userSid, StringComparison.OrdinalIgnoreCase))
        {
            subject += $":{clientId}";
        }

        return subject;
    }

    private void EnsureCacheCapacity()
    {
        if (_apiKeyCache.Count >= MaxCacheSize)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var kvp in _apiKeyCache)
            {
                if (kvp.Value.Expiry <= now)
                {
                    _apiKeyCache.TryRemove(kvp.Key, out _);
                }
            }

            // If still over capacity after removing expired entries, clear half
            if (_apiKeyCache.Count >= MaxCacheSize)
            {
                _apiKeyCache.Clear();
            }
        }
    }
}
