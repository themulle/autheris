namespace Autheris.Application.Dbt.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface ISemanticCatalogService
{
    string ManifestVersion => "1.0";
    Task RegisterManifestAsync(DbtSemanticManifest manifest, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DbtMetric>> ListMetricsAsync(string? modelOrDataset = null, CancellationToken cancellationToken = default);
    Task<DbtMetric?> GetMetricAsync(string metricName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DbtSemanticModel>> ListSemanticModelsAsync(CancellationToken cancellationToken = default);
    Task<DbtSemanticModel?> GetSemanticModelAsync(string modelName, CancellationToken cancellationToken = default);

    Task RegisterManifestAsync(DbtSemanticManifest manifest, TenantId tenantId, CancellationToken cancellationToken = default) =>
        RegisterManifestAsync(manifest, cancellationToken);
    Task<IReadOnlyList<DbtMetric>> ListMetricsAsync(string? modelOrDataset, TenantId tenantId, CancellationToken cancellationToken = default) =>
        ListMetricsAsync(modelOrDataset, cancellationToken);
    Task<DbtMetric?> GetMetricAsync(string metricName, TenantId tenantId, CancellationToken cancellationToken = default) =>
        GetMetricAsync(metricName, cancellationToken);
    Task<IReadOnlyList<DbtSemanticModel>> ListSemanticModelsAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
        ListSemanticModelsAsync(cancellationToken);
    Task<DbtSemanticModel?> GetSemanticModelAsync(string modelName, TenantId tenantId, CancellationToken cancellationToken = default) =>
        GetSemanticModelAsync(modelName, cancellationToken);
}

public interface IDbtSemanticManifestIngestionService
{
    Task<DbtSemanticManifest> ParseAndIngestAsync(string manifestJson, CancellationToken cancellationToken = default);
    Task<DbtSemanticManifest> ParseAndIngestAsync(string manifestJson, TenantId tenantId, CancellationToken cancellationToken = default) =>
        ParseAndIngestAsync(manifestJson, cancellationToken);
}
