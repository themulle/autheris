namespace Autheris.Application.Dbt.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Model;

public sealed class InMemorySemanticCatalog : ISemanticCatalogService
{
    private readonly ConcurrentDictionary<string, DbtMetric> _metrics = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DbtSemanticModel> _models = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _manifestVersion = "1.0";

    public string ManifestVersion => _manifestVersion;

    public Task RegisterManifestAsync(DbtSemanticManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!string.IsNullOrWhiteSpace(manifest.Version))
        {
            _manifestVersion = manifest.Version;
        }

        foreach (var model in manifest.SemanticModels)
        {
            _models[model.Name] = model;
        }

        foreach (var metric in manifest.Metrics)
        {
            _metrics[metric.Name] = string.IsNullOrWhiteSpace(metric.Version) && !string.IsNullOrWhiteSpace(manifest.Version)
                ? metric with { Version = manifest.Version }
                : metric;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DbtMetric>> ListMetricsAsync(string? modelOrDataset = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelOrDataset))
        {
            return Task.FromResult<IReadOnlyList<DbtMetric>>(_metrics.Values.OrderBy(m => m.Name).ToList());
        }

        // Filter metrics associated with the given model or measures in that model
        if (_models.TryGetValue(modelOrDataset, out var semanticModel))
        {
            var measureNames = new HashSet<string>(semanticModel.Measures.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            var matched = _metrics.Values
                .Where(m => m.Measures.Any(measureNames.Contains))
                .OrderBy(m => m.Name)
                .ToList();
            return Task.FromResult<IReadOnlyList<DbtMetric>>(matched);
        }

        return Task.FromResult<IReadOnlyList<DbtMetric>>(Array.Empty<DbtMetric>());
    }

    public Task<DbtMetric?> GetMetricAsync(string metricName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(metricName))
        {
            return Task.FromResult<DbtMetric?>(null);
        }

        _metrics.TryGetValue(metricName, out var metric);
        return Task.FromResult(metric);
    }

    public Task<IReadOnlyList<DbtSemanticModel>> ListSemanticModelsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<DbtSemanticModel>>(_models.Values.OrderBy(m => m.Name).ToList());
    }

    public Task<DbtSemanticModel?> GetSemanticModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return Task.FromResult<DbtSemanticModel?>(null);
        }

        _models.TryGetValue(modelName, out var model);
        return Task.FromResult(model);
    }
}
