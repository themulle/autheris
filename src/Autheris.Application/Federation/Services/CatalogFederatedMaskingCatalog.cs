namespace Autheris.Application.Federation.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Federation.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// SR-L-12: Derives federated masking classifications from the governance catalog. Columns flagged sensitive or carrying a
/// masking rule are mapped by normalized column name. The map is cached per tenant (never shared across tenants) for a short TTL.
/// </summary>
public sealed class CatalogFederatedMaskingCatalog : IFederatedMaskingCatalog
{
    private static readonly MaskingRule RedactRule = new() { RuleType = "REDACT", Replacement = "[REDACTED]" };

    private readonly ITableMetadataRepository _repository;
    private readonly IOptions<GatewayOptions> _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (IReadOnlyDictionary<string, MaskingRule> Map, DateTimeOffset LoadedAt, Autheris.Application.Catalog.CatalogVersion Version)> _cache = new(StringComparer.Ordinal);

    public CatalogFederatedMaskingCatalog(ITableMetadataRepository repository, IOptions<GatewayOptions> options, TimeProvider? time = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Drops the cached classifications (one tenant, or all when null) so a masking rule change applies immediately.</summary>
    public void Invalidate(TenantId? tenant = null)
    {
        if (tenant is { } t)
        {
            _cache.TryRemove(t.Value, out _);
        }
        else
        {
            _cache.Clear();
        }
    }

    public async ValueTask<IReadOnlyDictionary<string, MaskingRule>> GetClassificationsAsync(TenantId tenant, CancellationToken ct = default)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(0, _options.Value.Federation.CatalogMaskingCacheSeconds));
        var now = _time.GetUtcNow();
        // Every catalog write (so every masking rule write) bumps the persisted catalog version: a changed version drops the cached map.
        var version = await _repository.GetCatalogVersionAsync(ct).ConfigureAwait(false);
        if (_cache.TryGetValue(tenant.Value, out var entry) && now - entry.LoadedAt < ttl && entry.Version == version)
        {
            return entry.Map;
        }

        var tables = await _repository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, MaskingRule>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            // GHSA-7pjw: a table owned by another tenant never contributes (its weaker rule must not apply). A table without a recorded owner
            // still contributes: masking more is the safe side, and the catalog repositories do not persist the owner yet.
            if (!string.IsNullOrWhiteSpace(table.OwnerTenantId) && !string.Equals(table.OwnerTenantId, tenant.Value, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var column in table.Columns)
            {
                var hasRule = table.ColumnMaskingRules.TryGetValue(column.ColumnName, out var rule);
                if (!column.IsSensitive && !hasRule)
                {
                    continue;
                }

                var key = FederatedFieldName.Normalize(column.ColumnName);
                if (key.Length == 0) continue;

                // The same column name on several tables resolves to the strictest rule, whatever the load order.
                var candidate = hasRule && rule != null ? rule : RedactRule;
                if (!map.TryGetValue(key, out var current) ||
                    CatalogGovernanceRatchet.MaskingRuleStrength(candidate) > CatalogGovernanceRatchet.MaskingRuleStrength(current))
                {
                    map[key] = candidate;
                }
            }
        }

        _cache[tenant.Value] = (map, now, version);
        return map;
    }
}
