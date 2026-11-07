namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Profiling;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Endpoints.Services;
using Autheris.Application.Tenancy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Physical key and index direction reconciliation during dbt manifest ingestion (Issue #427).
/// </summary>
public sealed partial class DbtMetadataIngestionService
{
    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        IPolicyEpochRepository? epochRepository,
        IOptions<GatewayOptions>? gatewayOptions,
        SqlEndpointLoader? sqlEndpointLoader,
        ITableRelationRepository? relationRepository,
        VirtualFilterAdministrationService? virtualFilterAdmin,
        IRebacStore? rebacStore,
        IDatasetTagImportService? tagImport,
        ISyntheticSampleRowGenerator? syntheticRowGenerator,
        IDbtTestTargetMap? testTargetMap,
        IPhysicalKeyCatalog? physicalKeys,
        IAuditWriter? auditWriter,
        ILogger<DbtMetadataIngestionService> logger)
    {
        _tagImport = tagImport;
        _syntheticRowGenerator = syntheticRowGenerator;
        _testTargetMap = testTargetMap;
        _proposalRepository = proposalRepository ?? throw new ArgumentNullException(nameof(proposalRepository));
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _lineageGraphStore = lineageGraphStore ?? throw new ArgumentNullException(nameof(lineageGraphStore));
        _epochRepository = epochRepository;
        _gatewayOptions = gatewayOptions;
        _sqlEndpointLoader = sqlEndpointLoader;
        _relationRepository = relationRepository;
        _virtualFilterAdmin = virtualFilterAdmin;
        _rebacStore = rebacStore;
        _physicalKeys = physicalKeys;
        _auditWriter = auditWriter;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private async Task<TableMetadata> ReconcilePhysicalDirectionsIfApplicableAsync(
        TableMetadata updatedMetadata,
        TableMetadata? existingMeta,
        List<string> warnings,
        CancellationToken ct)
    {
        if (_physicalKeys is null || updatedMetadata.Table.DataSourceType != DataSourceType.Sql)
        {
            return updatedMetadata;
        }

        var physical = await _physicalKeys.ReadAsync(updatedMetadata, ct).ConfigureAwait(false);
        var reconciled = PhysicalKeyDirectionReconciler.Reconcile(updatedMetadata, existingMeta, physical);
        foreach (var finding in reconciled.Findings)
        {
            warnings.Add($"Table '{updatedMetadata.Identifier}': {finding}");
        }

        if (reconciled.PhysicalApplied)
        {
            if (await TryAuditPhysicalDirectionsAsync(updatedMetadata.Identifier, existingMeta, reconciled, ct).ConfigureAwait(false))
            {
                return reconciled.Metadata;
            }

            warnings.Add($"Table '{updatedMetadata.Identifier}': KEY_DIRECTION_AUDIT_FAILED");
            return reconciled.WithoutPhysical;
        }

        return updatedMetadata;
    }

    private async Task<bool> TryAuditPhysicalDirectionsAsync(
        TableIdentifier table,
        TableMetadata? existing,
        PhysicalKeyDirectionReconciler.Result reconciled,
        CancellationToken ct)
    {
        if (_auditWriter is null)
        {
            return false;
        }

        try
        {
            var tenant = existing?.OwnerTenantId is { } owner
                ? new TenantId(owner)
                : TenantResolver.EffectiveDefaultOf(_gatewayOptions?.Value?.Tenancy) ?? TenantId.Platform;

            await _auditWriter.RecordRequiredAsync(new AuditLogEntry
            {
                TenantId = tenant,
                EventType = AuditEventTypes.CatalogKeyDefinitionChanged,
                ActorSid = new Sid("system:dbt-sync"),
                TargetTable = table.ToString(),
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    datasetId = table.ToString(),
                    directionSource = "physical",
                    before = DirectionView(reconciled.WithoutPhysical),
                    after = DirectionView(reconciled.Metadata)
                })
            }, ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                _logger.LogWarning("Key direction audit cancelled for table {Table}; physical directions not applied.", table);
            }
            else
            {
                _logger.LogWarning(ex, "Key direction audit failed for table {Table}; physical directions not applied.", table);
            }

            return false;
        }
    }

    private static object DirectionView(TableMetadata metadata) => new
    {
        primaryKeyDirections = KeyDirections.ToWire(metadata.PrimaryKeyDirections),
        uniqueIndexDirections = metadata.Indexes.Where(static i => i.IsUnique).Select(static i => KeyDirections.ToWire(i.Directions)).ToArray()
    };
}
