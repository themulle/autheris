namespace Autheris.Application.Usage;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Resolves group SIDs to human-readable display names in precedence order:
/// <see cref="UsageAccountingOptions.GroupDisplayNames"/> -> SCIM identity store
/// (<c>IDENTITY_GROUPS.display_name</c> via <c>object_sid</c>) -> raw SID.
/// Never queries LDAP or matches non-SID identifiers.
/// </summary>
public sealed class UsageGroupNameResolver : IUsageGroupNameResolver
{
    private const int CacheCapacity = 10_000;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly IOptions<UsageAccountingOptions> _options;
    private readonly IScimGroupStore? _groupStore;
    private readonly BoundedCache<(string TenantId, string GroupSid), string> _cache;

    public UsageGroupNameResolver(
        IOptions<UsageAccountingOptions> options,
        IScimGroupStore? groupStore = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _groupStore = groupStore;
        _cache = new BoundedCache<(string TenantId, string GroupSid), string>(
            CacheCapacity,
            CacheTtl,
            timeProvider ?? TimeProvider.System);
    }

    public async Task<string> ResolveGroupNameAsync(TenantId tenant, string groupSid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(groupSid))
        {
            return groupSid ?? string.Empty;
        }

        string trimmed = groupSid.Trim();
        if (!SidFormat.IsGroupSid(trimmed))
        {
            return trimmed;
        }

        var cacheKey = (tenant.Value, trimmed);
        if (_cache.TryGet(cacheKey, out var cached))
        {
            return cached;
        }

        string? configured = TryResolveConfiguredName(trimmed);
        if (configured is not null)
        {
            _cache.Set(cacheKey, configured);
            return configured;
        }

        if (_groupStore is not null)
        {
            var canonicalSid = new Sid(trimmed);
            string? dbName = await _groupStore.GetGroupDisplayNameBySidAsync(tenant, canonicalSid, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(dbName))
            {
                _cache.Set(cacheKey, dbName);
                return dbName;
            }
        }

        _cache.Set(cacheKey, trimmed);
        return trimmed;
    }

    private string? TryResolveConfiguredName(string canonicalSid)
    {
        var map = _options.Value.GroupDisplayNames;
        if (map is null || map.Count == 0)
        {
            return null;
        }

        if (map.TryGetValue(canonicalSid, out var direct) && !string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        foreach (var kvp in map)
        {
            if (string.Equals(kvp.Key, canonicalSid, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(kvp.Value))
            {
                return kvp.Value;
            }
        }

        return null;
    }
}
