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
using Microsoft.Extensions.Options;

/// <summary>
/// Non-persistent <see cref="ISemanticCatalogService"/> for tests and <c>Semantic:PersistManifest = false</c>. Same tenancy rules as the
/// persistent catalog: a tenant sees only its own partition, an import replaces the whole partition.
/// </summary>
public sealed class InMemorySemanticCatalog : ISemanticCatalogService
{
    private readonly ConcurrentDictionary<string, SemanticCatalogPartition> _partitions = new(StringComparer.Ordinal);
    private readonly SemanticTenancy _tenancy;

    /// <param name="gatewayOptions">Source of <c>Gateway:Tenancy:Mode</c>; unset or unknown means Multi (SEC-112-13).</param>
    public InMemorySemanticCatalog(IOptions<GatewayOptions>? gatewayOptions = null)
    {
        _tenancy = SemanticTenancy.FromOptions(gatewayOptions);
    }

    public Task RegisterManifestAsync(DbtSemanticManifest manifest, TenantId tenantId, string? importedBy = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!_tenancy.TryMapKey(tenantId, out var key))
        {
            throw new GatewayForbiddenException();
        }

        _partitions[key.Value] = SemanticCatalogPartition.From(manifest);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DbtMetric>> ListMetricsAsync(string? modelOrDataset, TenantId tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Partition(tenantId).ListMetrics(modelOrDataset));

    public Task<DbtMetric?> GetMetricAsync(string metricName, TenantId tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Partition(tenantId).GetMetric(metricName));

    public Task<IReadOnlyList<DbtSemanticModel>> ListSemanticModelsAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Partition(tenantId).ListModels());

    public Task<DbtSemanticModel?> GetSemanticModelAsync(string modelName, TenantId tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Partition(tenantId).GetModel(modelName));

    public Task<string> GetManifestVersionAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Partition(tenantId).Version);

    private SemanticCatalogPartition Partition(TenantId tenantId) =>
        _tenancy.TryMapKey(tenantId, out var key) && _partitions.TryGetValue(key.Value, out var partition)
            ? partition
            : SemanticCatalogPartition.Empty;
}
