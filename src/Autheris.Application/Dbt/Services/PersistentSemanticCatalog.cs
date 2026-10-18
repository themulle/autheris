namespace Autheris.Application.Dbt.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Per-tenant read-through cache over <see cref="ISemanticManifestStore"/> (#112 A-9). The first access of a tenant loads its row
/// (single flight per tenant); after <c>Semantic:ManifestCacheSeconds</c> the stored content hash is re-checked so other nodes pick up an
/// import. A row that cannot be read (bad JSON, unknown format version, hash mismatch) is "no manifest" with one Error log: fail closed,
/// never a startup failure. The store holds data only; it never grants access.
/// </summary>
public sealed partial class PersistentSemanticCatalog : ISemanticCatalogService, IDisposable
{
    private sealed class TenantEntry : IDisposable
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public volatile Snapshot? Current;

        public void Dispose() => Gate.Dispose();
    }

    private sealed record Snapshot(SemanticCatalogPartition Partition, string? ContentHash, DateTimeOffset CheckedAt);

    private readonly ISemanticManifestStore _store;
    private readonly SemanticTenancy _tenancy;
    private readonly ILogger<PersistentSemanticCatalog> _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, TenantEntry> _entries = new(StringComparer.Ordinal);

    public PersistentSemanticCatalog(
        ISemanticManifestStore store,
        IOptions<SemanticOptions> options,
        IOptions<GatewayOptions>? gatewayOptions = null,
        ILogger<PersistentSemanticCatalog>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(options);
        _tenancy = SemanticTenancy.FromOptions(gatewayOptions);
        _logger = logger ?? NullLogger<PersistentSemanticCatalog>.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _ttl = TimeSpan.FromSeconds(Math.Clamp(options.Value.ManifestCacheSeconds, 0, SemanticOptions.MaxManifestCacheSeconds));
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }
    }

    public async Task RegisterManifestAsync(DbtSemanticManifest manifest, TenantId tenantId, string? importedBy = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!_tenancy.TryMapKey(tenantId, out var key))
        {
            throw new GatewayForbiddenException();
        }

        var json = SemanticManifestJson.Serialize(manifest);
        var hash = SemanticManifestJson.ComputeHash(json);
        var version = string.IsNullOrWhiteSpace(manifest.Version) ? SemanticCatalogPartition.DefaultVersion : manifest.Version;
        var stored = new StoredSemanticManifest(
            key, version, hash, SemanticManifestJson.FormatVersion, manifest.SemanticModels.Count, manifest.Metrics.Count, json,
            string.IsNullOrWhiteSpace(importedBy) ? "system" : importedBy, _time.GetUtcNow());

        var entry = _entries.GetOrAdd(key.Value, static _ => new TenantEntry());
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(key, stored, cancellationToken).ConfigureAwait(false);
            // The local view is swapped only after the row is durable: a failed save leaves the previous manifest in place.
            entry.Current = new Snapshot(SemanticCatalogPartition.From(manifest), hash, _time.GetUtcNow());
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<IReadOnlyList<DbtMetric>> ListMetricsAsync(string? modelOrDataset, TenantId tenantId, CancellationToken cancellationToken = default) =>
        (await PartitionAsync(tenantId, cancellationToken).ConfigureAwait(false)).ListMetrics(modelOrDataset);

    public async Task<DbtMetric?> GetMetricAsync(string metricName, TenantId tenantId, CancellationToken cancellationToken = default) =>
        (await PartitionAsync(tenantId, cancellationToken).ConfigureAwait(false)).GetMetric(metricName);

    public async Task<IReadOnlyList<DbtSemanticModel>> ListSemanticModelsAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
        (await PartitionAsync(tenantId, cancellationToken).ConfigureAwait(false)).ListModels();

    public async Task<DbtSemanticModel?> GetSemanticModelAsync(string modelName, TenantId tenantId, CancellationToken cancellationToken = default) =>
        (await PartitionAsync(tenantId, cancellationToken).ConfigureAwait(false)).GetModel(modelName);

    public async Task<string> GetManifestVersionAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
        (await PartitionAsync(tenantId, cancellationToken).ConfigureAwait(false)).Version;

    private async ValueTask<SemanticCatalogPartition> PartitionAsync(TenantId tenantId, CancellationToken ct)
    {
        if (!_tenancy.TryMapKey(tenantId, out var key))
        {
            return SemanticCatalogPartition.Empty;
        }

        var entry = _entries.GetOrAdd(key.Value, static _ => new TenantEntry());
        var current = entry.Current;
        if (current is not null && !IsStale(current))
        {
            return current.Partition;
        }

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = entry.Current;
            if (current is not null && !IsStale(current))
            {
                return current.Partition;
            }

            var row = await _store.GetAsync(key, ct).ConfigureAwait(false);
            if (row is not null && current is not null && string.Equals(row.ContentHash, current.ContentHash, StringComparison.Ordinal))
            {
                entry.Current = current with { CheckedAt = _time.GetUtcNow() };
                return current.Partition;
            }

            var loaded = row is null
                ? new Snapshot(SemanticCatalogPartition.Empty, null, _time.GetUtcNow())
                : Load(row);
            entry.Current = loaded;
            return loaded.Partition;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private bool IsStale(Snapshot snapshot) => _time.GetUtcNow() - snapshot.CheckedAt >= _ttl;

    private Snapshot Load(StoredSemanticManifest row)
    {
        var now = _time.GetUtcNow();
        if (row.FormatVersion != SemanticManifestJson.FormatVersion)
        {
            LogUnreadable(row.TenantId.Value, "unknown format version");
            return new Snapshot(SemanticCatalogPartition.Empty, row.ContentHash, now);
        }

        if (!string.Equals(SemanticManifestJson.ComputeHash(row.Json), row.ContentHash, StringComparison.Ordinal))
        {
            LogUnreadable(row.TenantId.Value, "content hash mismatch");
            return new Snapshot(SemanticCatalogPartition.Empty, row.ContentHash, now);
        }

        var manifest = SemanticManifestJson.TryDeserialize(row.Json);
        if (manifest is null)
        {
            LogUnreadable(row.TenantId.Value, "manifest json is not readable");
            return new Snapshot(SemanticCatalogPartition.Empty, row.ContentHash, now);
        }

        return new Snapshot(SemanticCatalogPartition.From(manifest), row.ContentHash, now);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "The persisted semantic manifest of tenant {TenantId} is unusable ({Reason}); the tenant sees no manifest until it is imported again.")]
    private partial void LogUnreadable(string tenantId, string reason);
}
