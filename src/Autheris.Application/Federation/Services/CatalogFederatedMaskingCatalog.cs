namespace Autheris.Application.Federation.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Federation.Interfaces;
using Autheris.Application.Interfaces;
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
    private readonly ConcurrentDictionary<string, (IReadOnlyDictionary<string, MaskingRule> Map, DateTimeOffset LoadedAt)> _cache = new(StringComparer.Ordinal);

    public CatalogFederatedMaskingCatalog(ITableMetadataRepository repository, IOptions<GatewayOptions> options, TimeProvider? time = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
    }

    public async ValueTask<IReadOnlyDictionary<string, MaskingRule>> GetClassificationsAsync(TenantId tenant, CancellationToken ct = default)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(0, _options.Value.Federation.CatalogMaskingCacheSeconds));
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(tenant.Value, out var entry) && now - entry.LoadedAt < ttl)
        {
            return entry.Map;
        }

        var tables = await _repository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, MaskingRule>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            foreach (var column in table.Columns)
            {
                var hasRule = table.ColumnMaskingRules.TryGetValue(column.ColumnName, out var rule);
                if (!column.IsSensitive && !hasRule)
                {
                    continue;
                }

                var key = FederatedFieldName.Normalize(column.ColumnName);
                if (key.Length == 0) continue;

                // A concrete masking rule wins over the generic redaction of a sensitive-only column.
                if (hasRule && rule != null)
                {
                    map[key] = rule;
                }
                else
                {
                    map.TryAdd(key, RedactRule);
                }
            }
        }

        _cache[tenant.Value] = (map, now);
        return map;
    }
}
