namespace Autheris.Application.Caching.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
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

        // 2. Check JWT Claims, Scopes, or RoleTierMappings from Principal
        if (principal?.Identity?.IsAuthenticated == true)
        {
            var canonicalScope = GetCanonicalScopeIdentifier(principal);
            var subjectId = BuildAuthenticatedSubjectId(principal, canonicalScope);

            // Determine user/role ceiling (anti-escalation maximum tier, SEC-GOV29-01)
            var tierClaim = principal.FindFirst("tier")?.Value ??
                            principal.FindFirst("client_tier")?.Value ??
                            principal.FindFirst("urn:autheris:tier")?.Value;

            ClientTier? explicitClaimTier = null;
            if (!string.IsNullOrWhiteSpace(tierClaim))
            {
                if (Enum.TryParse<ClientTier>(tierClaim, true, out var parsedClaimTier))
                {
                    explicitClaimTier = parsedClaimTier;
                }
                else
                {
                    // SEC-GOV29-01: Any unrecognized or unparseable tier string specified in configuration or claims MUST fail closed to the lowest safe default
                    explicitClaimTier = ClientTier.Standard;
                }
            }

            // Check Role-to-Tier mappings
            ClientTier? highestRoleTier = null;
            if (tierOptions?.RoleTierMappings != null && tierOptions.RoleTierMappings.Count > 0)
            {
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
            }

            ClientTier maxPermittedTier;
            if (explicitClaimTier.HasValue && highestRoleTier.HasValue)
            {
                maxPermittedTier = explicitClaimTier.Value > highestRoleTier.Value ? explicitClaimTier.Value : highestRoleTier.Value;
            }
            else if (explicitClaimTier.HasValue)
            {
                maxPermittedTier = explicitClaimTier.Value;
            }
            else if (highestRoleTier.HasValue)
            {
                maxPermittedTier = highestRoleTier.Value;
            }
            else
            {
                maxPermittedTier = ClientTier.Standard;
            }

            // Check Scope-to-Tier mappings and ScopeLimits (prioritize scope mapping ahead of default role fallback, clamped by anti-escalation ceiling)
            var scopes = Autheris.Domain.Security.TokenAccessScope.GetScopeSet(principal.Claims);
            ClientTier? matchedScopeTier = null;
            ClientTierLimitOverride? matchedScopeLimit = null;

            if (scopes != null && scopes.Count > 0)
            {
                foreach (var rawScope in scopes)
                {
                    if (string.IsNullOrWhiteSpace(rawScope)) continue;
                    var bareScope = Autheris.Domain.Security.ScopeNames.ToBareName(rawScope.Trim());
                    if (string.IsNullOrWhiteSpace(bareScope)) continue;

                    if (tierOptions?.ScopeTierMappings != null && tierOptions.ScopeTierMappings.TryGetValue(bareScope, out var mappedTierStr))
                    {
                        if (Enum.TryParse<ClientTier>(mappedTierStr, true, out var mappedScopeTierVal))
                        {
                            if (matchedScopeTier == null || mappedScopeTierVal > matchedScopeTier.Value)
                            {
                                matchedScopeTier = mappedScopeTierVal;
                            }
                        }
                        else
                        {
                            // SEC-GOV29-01: unparseable fails closed to lowest safe default
                            matchedScopeTier ??= ClientTier.Standard;
                        }
                    }

                    if (tierOptions?.ScopeLimits != null && tierOptions.ScopeLimits.TryGetValue(bareScope, out var scopeLimitDef))
                    {
                        matchedScopeLimit ??= scopeLimitDef;
                    }
                }
            }

            ClientTier effectiveTier;
            if (matchedScopeTier.HasValue)
            {
                // SEC-GOV29-01: Clamped to caller's maximum permitted user/role tier (anti-escalation)
                effectiveTier = matchedScopeTier.Value > maxPermittedTier ? maxPermittedTier : matchedScopeTier.Value;
            }
            else
            {
                // Fallback to claim / role / standard
                effectiveTier = explicitClaimTier ?? highestRoleTier ?? ClientTier.Standard;
            }

            ClientQuotaPolicy effectivePolicy;
            if (matchedScopeLimit != null)
            {
                effectivePolicy = ClientQuotaPolicy.ForScopeLimit(effectiveTier, matchedScopeLimit, tierOptions);
            }
            else
            {
                effectivePolicy = ClientQuotaPolicy.ForTier(effectiveTier, tierOptions);
            }

            return Task.FromResult(new ClientQuotaContext(subjectId, effectiveTier, effectivePolicy, canonicalScope));
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
    /// SEC-GOV29-02: Scope canonicalization, delimiter sanitization, DoS bounds and ordinal sorting.
    /// </summary>
    internal static string? GetCanonicalScopeIdentifier(ClaimsPrincipal? principal)
    {
        if (principal == null) return null;

        var rawScopes = Autheris.Domain.Security.TokenAccessScope.GetScopeSet(principal.Claims);
        if (rawScopes == null || rawScopes.Count == 0) return null;

        var sanitizedScopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawScopes)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var bare = Autheris.Domain.Security.ScopeNames.ToBareName(raw.Trim());
            if (string.IsNullOrWhiteSpace(bare)) continue;

            // SEC-GOV29-02: Sanitize delimiters (:, +, {, }, \r, \n) to prevent subject key or Redis injection
            var sb = new StringBuilder(bare.Length);
            foreach (var ch in bare)
            {
                if (ch != ':' && ch != '+' && ch != '{' && ch != '}' && ch != '\r' && ch != '\n')
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
            }

            var clean = sb.ToString().Trim();
            if (!string.IsNullOrEmpty(clean))
            {
                sanitizedScopes.Add(clean);
                if (sanitizedScopes.Count >= 32) // SEC-GOV29-02: DoS guard, max 32 distinct scopes
                {
                    break;
                }
            }
        }

        if (sanitizedScopes.Count == 0) return null;

        var sorted = sanitizedScopes.OrderBy(s => s, StringComparer.Ordinal).ToList();
        var canonical = string.Join("+", sorted);
        if (canonical.Length > 256) // SEC-GOV29-02: Max 256 characters
        {
            canonical = canonical[..256];
        }

        return canonical;
    }

    /// <summary>
    /// SEC M-16 &amp; PLAN-GOV-29: Quota-Subjekt eines angemeldeten Aufrufers = Tenant + Benutzer-SID + client_id + canonicalScope.
    /// </summary>
    internal static string BuildAuthenticatedSubjectId(ClaimsPrincipal principal)
    {
        var canonicalScope = GetCanonicalScopeIdentifier(principal);
        return BuildAuthenticatedSubjectId(principal, canonicalScope);
    }

    internal static string BuildAuthenticatedSubjectId(ClaimsPrincipal principal, string? canonicalScope)
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

        if (!string.IsNullOrWhiteSpace(canonicalScope))
        {
            subject += $":{canonicalScope}";
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
