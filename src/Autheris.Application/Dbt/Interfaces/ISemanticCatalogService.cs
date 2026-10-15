namespace Autheris.Application.Dbt.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface ISemanticCatalogService
{
    string ManifestVersion => "1.0";
    Task RegisterManifestAsync(DbtSemanticManifest manifest, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DbtMetric>> ListMetricsAsync(string? modelOrDataset = null, CancellationToken cancellationToken = default);
    Task<DbtMetric?> GetMetricAsync(string metricName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DbtSemanticModel>> ListSemanticModelsAsync(CancellationToken cancellationToken = default);
    Task<DbtSemanticModel?> GetSemanticModelAsync(string modelName, CancellationToken cancellationToken = default);
}

public interface IDbtSemanticManifestIngestionService
{
    Task<DbtSemanticManifest> ParseAndIngestAsync(string manifestJson, CancellationToken cancellationToken = default);
}
