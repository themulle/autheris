namespace Autheris.Application.Dbt.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed class InMemorySemanticCatalog : ISemanticCatalogService
{
    private sealed class TenantCatalogPartition
    {
        public ConcurrentDictionary<string, DbtMetric> Metrics { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, DbtSemanticModel> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
        public volatile string ManifestVersion = "1.0";
    }

    private readonly ConcurrentDictionary<string, TenantCatalogPartition> _partitions = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _manifestVersion = "1.0";

    public string ManifestVersion => _manifestVersion;

    public Task RegisterManifestAsync(DbtSemanticManifest manifest, CancellationToken cancellationToken = default) =>
        RegisterManifestAsync(manifest, TenantId.LegacySingleTenant, cancellationToken);

    public Task RegisterManifestAsync(DbtSemanticManifest manifest, TenantId tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var partition = _partitions.GetOrAdd(tenantId.Value, _ => new TenantCatalogPartition());

        if (!string.IsNullOrWhiteSpace(manifest.Version))
        {
            partition.ManifestVersion = manifest.Version;
            _manifestVersion = manifest.Version;
        }

        foreach (var model in manifest.SemanticModels)
        {
            partition.Models[model.Name] = model;
        }

        foreach (var metric in manifest.Metrics)
        {
            partition.Metrics[metric.Name] = string.IsNullOrWhiteSpace(metric.Version) && !string.IsNullOrWhiteSpace(manifest.Version)
                ? metric with { Version = manifest.Version }
                : metric;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DbtMetric>> ListMetricsAsync(string? modelOrDataset = null, CancellationToken cancellationToken = default) =>
        ListMetricsAsync(modelOrDataset, TenantId.LegacySingleTenant, cancellationToken);

    public Task<IReadOnlyList<DbtMetric>> ListMetricsAsync(string? modelOrDataset, TenantId tenantId, CancellationToken cancellationToken = default)
    {
        var partition = ResolvePartition(tenantId);
        if (partition == null)
        {
            return Task.FromResult<IReadOnlyList<DbtMetric>>(Array.Empty<DbtMetric>());
        }

        if (string.IsNullOrWhiteSpace(modelOrDataset))
        {
            return Task.FromResult<IReadOnlyList<DbtMetric>>(partition.Metrics.Values.OrderBy(m => m.Name).ToList());
        }

        if (partition.Models.TryGetValue(modelOrDataset, out var semanticModel))
        {
            var measureNames = new HashSet<string>(semanticModel.Measures.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            var matched = partition.Metrics.Values
                .Where(m => m.Measures.Any(measureNames.Contains))
                .OrderBy(m => m.Name)
                .ToList();
            return Task.FromResult<IReadOnlyList<DbtMetric>>(matched);
        }

        return Task.FromResult<IReadOnlyList<DbtMetric>>(Array.Empty<DbtMetric>());
    }

    public Task<DbtMetric?> GetMetricAsync(string metricName, CancellationToken cancellationToken = default) =>
        GetMetricAsync(metricName, TenantId.LegacySingleTenant, cancellationToken);

    public Task<DbtMetric?> GetMetricAsync(string metricName, TenantId tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(metricName))
        {
            return Task.FromResult<DbtMetric?>(null);
        }

        var partition = ResolvePartition(tenantId);
        if (partition != null && partition.Metrics.TryGetValue(metricName, out var metric))
        {
            return Task.FromResult<DbtMetric?>(metric);
        }

        return Task.FromResult<DbtMetric?>(null);
    }

    public Task<IReadOnlyList<DbtSemanticModel>> ListSemanticModelsAsync(CancellationToken cancellationToken = default) =>
        ListSemanticModelsAsync(TenantId.LegacySingleTenant, cancellationToken);

    public Task<IReadOnlyList<DbtSemanticModel>> ListSemanticModelsAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        var partition = ResolvePartition(tenantId);
        if (partition == null)
        {
            return Task.FromResult<IReadOnlyList<DbtSemanticModel>>(Array.Empty<DbtSemanticModel>());
        }

        return Task.FromResult<IReadOnlyList<DbtSemanticModel>>(partition.Models.Values.OrderBy(m => m.Name).ToList());
    }

    public Task<DbtSemanticModel?> GetSemanticModelAsync(string modelName, CancellationToken cancellationToken = default) =>
        GetSemanticModelAsync(modelName, TenantId.LegacySingleTenant, cancellationToken);

    public Task<DbtSemanticModel?> GetSemanticModelAsync(string modelName, TenantId tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return Task.FromResult<DbtSemanticModel?>(null);
        }

        var partition = ResolvePartition(tenantId);
        if (partition != null && partition.Models.TryGetValue(modelName, out var model))
        {
            return Task.FromResult<DbtSemanticModel?>(model);
        }

        return Task.FromResult<DbtSemanticModel?>(null);
    }

    private TenantCatalogPartition? ResolvePartition(TenantId tenantId)
    {
        if (_partitions.TryGetValue(tenantId.Value, out var partition))
        {
            return partition;
        }

        if (_partitions.TryGetValue(TenantId.LegacySingleTenant.Value, out var fallback))
        {
            return fallback;
        }

        return null;
    }
}
