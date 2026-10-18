namespace Autheris.Application.Dbt.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Tenant-explicit semantic catalog (#112 A-9, F-6): there are no tenant-less members and no fallback to another tenant's data. A tenant
/// sees only its own manifest; in <c>Single</c> tenancy mode every tenant id maps to the one legacy partition.
/// </summary>
public interface ISemanticCatalogService
{
    /// <summary>Replaces the whole manifest of the tenant (removed metrics disappear). Throws <c>GatewayForbiddenException</c> for a tenant that has no catalog standing.</summary>
    Task RegisterManifestAsync(DbtSemanticManifest manifest, TenantId tenantId, string? importedBy = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DbtMetric>> ListMetricsAsync(string? modelOrDataset, TenantId tenantId, CancellationToken cancellationToken = default);
    Task<DbtMetric?> GetMetricAsync(string metricName, TenantId tenantId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DbtSemanticModel>> ListSemanticModelsAsync(TenantId tenantId, CancellationToken cancellationToken = default);
    Task<DbtSemanticModel?> GetSemanticModelAsync(string modelName, TenantId tenantId, CancellationToken cancellationToken = default);

    /// <summary>The manifest version of the tenant's own partition (<c>1.0</c> when none was imported). Never reflects another tenant's import.</summary>
    Task<string> GetManifestVersionAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}

public interface IDbtSemanticManifestIngestionService
{
    /// <summary>Parses, validates and persists the manifest of <paramref name="tenantId"/> (the server-resolved request tenant).</summary>
    Task<DbtSemanticManifest> ParseAndIngestAsync(string manifestJson, TenantId tenantId, string? importedBy = null, CancellationToken cancellationToken = default);
}
