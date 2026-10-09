using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

namespace Autheris.Application.Interfaces;

public interface IConsentCacheService
{
    Task<TableAccessDecision?> GetCachedDecisionAsync(Sid userSid, TableIdentifier table, string? contextHash = null, CancellationToken ct = default)
        => GetCachedDecisionAsync(TenantId.LegacySingleTenant, userSid, table, contextHash, ct);

    Task<TableAccessDecision?> GetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, string? contextHash = null, CancellationToken ct = default);

    Task<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>> GetCachedDecisionsAsync(
        TenantId tenant,
        Sid userSid,
        IReadOnlyList<TableIdentifier> tables,
        string? contextHash = null,
        CancellationToken ct = default);

    Task SetCachedDecisionAsync(Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, string? contextHash = null, CancellationToken ct = default)
        => SetCachedDecisionAsync(TenantId.LegacySingleTenant, userSid, table, decision, ttl, contextHash, ct);

    Task SetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, string? contextHash = null, CancellationToken ct = default);

    /// <summary>
    /// RR-L4-06: Returns the invalidation epoch of <paramref name="table"/>. Must be read <b>before</b> consents are loaded
    /// from the governance store and passed to the compare-and-set overload of <c>SetCachedDecisionAsync</c>.
    /// Returns <c>null</c> when the implementation does not support epoch snapshots.
    /// </summary>
    Task<long?> GetEpochSnapshotAsync(TableIdentifier table, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

    /// <summary>
    /// RR-L4-06: Compare-and-set variant. The decision is cached only if the table epoch is still
    /// <paramref name="epochAtLoad"/>; a revocation that happened between loading and caching therefore never
    /// gets "laundered" into a cache entry carrying the new epoch.
    /// </summary>
    Task SetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, string? contextHash, long? epochAtLoad, CancellationToken ct = default)
        => SetCachedDecisionAsync(tenant, userSid, table, decision, ttl, contextHash, ct);

    Task EvictTableDecisionsAsync(TableIdentifier table, CancellationToken ct = default);
    Task ClearL1CacheAsync(CancellationToken ct = default);

    public static string ComputeSubjectContextHash(IReadOnlySet<Sid>? groupSids, IReadOnlySet<string>? roles)
    {
        var groups = groupSids != null && groupSids.Count > 0
            ? string.Join(";", groupSids.Select(s => s.Value.ToUpperInvariant()).OrderBy(s => s, StringComparer.Ordinal))
            : string.Empty;
        var r = roles != null && roles.Count > 0
            ? string.Join(";", roles.Select(x => x.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal))
            : string.Empty;
        if (string.IsNullOrEmpty(groups) && string.IsNullOrEmpty(r)) return "default";
        var bytes = Encoding.UTF8.GetBytes($"{groups}|{r}");
        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }
}

