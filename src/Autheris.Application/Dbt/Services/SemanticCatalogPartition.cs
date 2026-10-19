namespace Autheris.Application.Dbt.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;

/// <summary>Immutable read view of one tenant's manifest: lookups by metric and model name (case-insensitive), listing and version.</summary>
internal sealed class SemanticCatalogPartition
{
    internal const string DefaultVersion = "1.0";

    public static readonly SemanticCatalogPartition Empty = new(DefaultVersion, new Dictionary<string, DbtMetric>(), new Dictionary<string, DbtSemanticModel>());

    private readonly Dictionary<string, DbtMetric> _metrics;
    private readonly Dictionary<string, DbtSemanticModel> _models;

    private SemanticCatalogPartition(string version, Dictionary<string, DbtMetric> metrics, Dictionary<string, DbtSemanticModel> models)
    {
        Version = version;
        _metrics = metrics;
        _models = models;
    }

    public string Version { get; }

    public static SemanticCatalogPartition From(DbtSemanticManifest manifest)
    {
        var version = string.IsNullOrWhiteSpace(manifest.Version) ? DefaultVersion : manifest.Version;
        var metrics = new Dictionary<string, DbtMetric>(StringComparer.OrdinalIgnoreCase);
        foreach (var metric in manifest.Metrics)
        {
            metrics[metric.Name] = string.IsNullOrWhiteSpace(metric.Version) ? metric with { Version = version } : metric;
        }

        var models = new Dictionary<string, DbtSemanticModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in manifest.SemanticModels)
        {
            models[model.Name] = model;
        }

        return new SemanticCatalogPartition(version, metrics, models);
    }

    public DbtMetric? GetMetric(string name) => string.IsNullOrWhiteSpace(name) ? null : _metrics.GetValueOrDefault(name);

    public DbtSemanticModel? GetModel(string name) => string.IsNullOrWhiteSpace(name) ? null : _models.GetValueOrDefault(name);

    public IReadOnlyList<DbtSemanticModel> ListModels() => _models.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();

    public IReadOnlyList<DbtMetric> ListMetrics(string? modelOrDataset)
    {
        if (string.IsNullOrWhiteSpace(modelOrDataset))
        {
            return _metrics.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        }

        if (_models.TryGetValue(modelOrDataset, out var semanticModel))
        {
            var measureNames = new HashSet<string>(semanticModel.Measures.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            return _metrics.Values
                .Where(m => m.Measures.Any(measureNames.Contains))
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToList();
        }

        return [];
    }
}
