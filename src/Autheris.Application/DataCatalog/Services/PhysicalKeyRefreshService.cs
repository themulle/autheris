namespace Autheris.Application.DataCatalog.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Reconciles physical key directions and non-unique physical indexes for declared tables of a datasource.
/// </summary>
public sealed class PhysicalKeyRefreshService : IPhysicalKeyRefreshService
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IPhysicalKeyCatalog _physicalKeyCatalog;
    private readonly IAuditWriter _auditWriter;
    private readonly IEpochValidationService _epochService;
    private readonly IPhysicalIndexRepository? _physicalIndexRepository;
    private readonly ILogger<PhysicalKeyRefreshService> _logger;

    public PhysicalKeyRefreshService(
        ITableMetadataRepository metadataRepository,
        IPhysicalKeyCatalog physicalKeyCatalog,
        IAuditWriter auditWriter,
        IEpochValidationService epochService,
        IPhysicalIndexRepository? physicalIndexRepository = null,
        ILogger<PhysicalKeyRefreshService>? logger = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _physicalKeyCatalog = physicalKeyCatalog ?? throw new ArgumentNullException(nameof(physicalKeyCatalog));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _epochService = epochService ?? throw new ArgumentNullException(nameof(epochService));
        _physicalIndexRepository = physicalIndexRepository;
        _logger = logger ?? NullLogger<PhysicalKeyRefreshService>.Instance;
    }

    public Task<PhysicalKeyRefreshResult> RefreshDatasourceKeysAsync(
        string datasourceId,
        PhysicalKeyRefreshRequest request,
        Sid actor,
        CancellationToken ct = default)
        => RefreshDatasourceKeysAsync(datasourceId, request, actor, null, ct);

    public async Task<PhysicalKeyRefreshResult> RefreshDatasourceKeysAsync(
        string datasourceId,
        PhysicalKeyRefreshRequest request,
        Sid actor,
        TenantId? restrictToTenant,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasourceId);
        ArgumentNullException.ThrowIfNull(request);

        var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var sourceTables = allTables
            .Where(t => t.Table.SourceName.Equals(datasourceId, StringComparison.OrdinalIgnoreCase) ||
                        t.Identifier.Domain.Equals(datasourceId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sourceTables.Count == 0)
        {
            throw new KeyNotFoundException($"Datasource '{datasourceId}' was not found.");
        }

        var tables = sourceTables;
        if (restrictToTenant is { } tenant && !string.IsNullOrWhiteSpace(tenant.Value))
        {
            tables = tables.Where(t =>
                string.Equals(t.OwnerTenantId, tenant.Value, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(request.TableFilter))
        {
            var filter = request.TableFilter.Trim();
            tables = tables.Where(t =>
                t.Identifier.TableName.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
                t.Identifier.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var details = new List<TableKeyRefreshItem>();
        var keysUpdated = 0;
        var indexesUpdated = 0;

        foreach (var table in tables)
        {
            var tableChanged = false;

            if (table.Table.DataSourceType != DataSourceType.Sql)
            {
                details.Add(new TableKeyRefreshItem(
                    table.Identifier.ToString(),
                    "Unsupported",
                    ["DataSourceType is not SQL."],
                    KeyDirections.ToWire(table.PrimaryKeyDirections)));
                continue;
            }

            var snapshot = await _physicalKeyCatalog.ReadAsync(table, ct).ConfigureAwait(false);
            if (snapshot.State == PhysicalKeySnapshotState.Unsupported)
            {
                details.Add(new TableKeyRefreshItem(
                    table.Identifier.ToString(),
                    "Unsupported",
                    ["Dialect does not support physical key direction inspection."],
                    KeyDirections.ToWire(table.PrimaryKeyDirections)));
                continue;
            }

            if (snapshot.State == PhysicalKeySnapshotState.Unavailable)
            {
                details.Add(new TableKeyRefreshItem(
                    table.Identifier.ToString(),
                    "Unavailable",
                    ["Physical key read was unavailable or failed."],
                    KeyDirections.ToWire(table.PrimaryKeyDirections)));
                continue;
            }

            var incoming = table with { ReplaceKeyDefinition = true };
            var reconciled = PhysicalKeyDirectionReconciler.Reconcile(incoming, table, snapshot);

            var status = "Unchanged";
            if (reconciled.Findings.Count > 0)
            {
                status = reconciled.Findings.Contains(PhysicalKeyDirectionReconciler.DriftCode)
                    ? "DriftDetected"
                    : "Unchanged";
            }

            if (reconciled.PhysicalApplied)
            {
                status = "Updated";
                keysUpdated++;

                if (!request.DryRun)
                {
                    var audited = await TryAuditAsync(table, reconciled, actor, ct).ConfigureAwait(false);
                    if (audited)
                    {
                        await _metadataRepository.ReplaceTableKeyDefinitionAsync(
                            table.Identifier,
                            reconciled.Metadata.PrimaryKeyColumns,
                            reconciled.Metadata.Indexes,
                            reconciled.Metadata.PrimaryKeyDirections,
                            ct).ConfigureAwait(false);

                        tableChanged = true;
                    }
                    else
                    {
                        _logger.LogWarning("Audit failed for table {Table}; physical directions not applied.", table.Identifier);
                        status = "AuditFailed";
                        keysUpdated--;
                    }
                }
            }

            // Sync non-unique physical indexes if available
            if (_physicalIndexRepository is not null && snapshot.NonUnique.State == PhysicalKeySnapshotState.Known)
            {
                var desiredIndexes = PhysicalNonUniqueIndexResolver.Resolve(table, snapshot);
                if (desiredIndexes is not null && !PhysicalNonUniqueIndexResolver.SameList(table.PhysicalIndexes, desiredIndexes))
                {
                    if (!request.DryRun)
                    {
                        var audited = await TryAuditNonUniqueAsync(table, table.PhysicalIndexes.Count, desiredIndexes.Count, actor, ct).ConfigureAwait(false);
                        if (audited)
                        {
                            try
                            {
                                await _physicalIndexRepository.ReplacePhysicalIndexesAsync(table.Identifier, desiredIndexes, ct).ConfigureAwait(false);
                                indexesUpdated++;
                                tableChanged = true;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                            {
                                _logger.LogWarning(ex, "Failed to persist physical non-unique indexes for table {Table}", table.Identifier);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Audit failed for non-unique indexes on table {Table}; physical indexes not applied.", table.Identifier);
                        }
                    }
                    else
                    {
                        indexesUpdated++;
                    }
                }
            }

            if (tableChanged)
            {
                await _epochService.InvalidateEpochAsync(table.Identifier, ct).ConfigureAwait(false);
            }

            details.Add(new TableKeyRefreshItem(
                table.Identifier.ToString(),
                status,
                reconciled.Findings,
                KeyDirections.ToWire(reconciled.Metadata.PrimaryKeyDirections)));
        }

        return new PhysicalKeyRefreshResult(
            DatasourceId: datasourceId,
            DryRun: request.DryRun,
            TablesScanned: tables.Count,
            KeysUpdated: keysUpdated,
            IndexesUpdated: indexesUpdated,
            Details: details);
    }

    private async Task<bool> TryAuditAsync(
        TableMetadata table,
        PhysicalKeyDirectionReconciler.Result reconciled,
        Sid actor,
        CancellationToken ct)
    {
        try
        {
            var tenant = table.OwnerTenantId is { } owner
                ? new TenantId(owner)
                : TenantId.Platform;

            await _auditWriter.RecordRequiredAsync(new AuditLogEntry
            {
                TenantId = tenant,
                EventType = AuditEventTypes.CatalogKeyDefinitionChanged,
                ActorSid = actor,
                TargetTable = table.Identifier.ToString(),
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    datasetId = table.Identifier.ToString(),
                    directionSource = "physical",
                    before = DirectionView(reconciled.WithoutPhysical),
                    after = DirectionView(reconciled.Metadata)
                })
            }, ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit record failed for table {Table}; changes will fail closed.", table.Identifier);
            return false;
        }
    }

    private async Task<bool> TryAuditNonUniqueAsync(
        TableMetadata table,
        int beforeCount,
        int afterCount,
        Sid actor,
        CancellationToken ct)
    {
        try
        {
            var tenant = table.OwnerTenantId is { } owner
                ? new TenantId(owner)
                : TenantId.Platform;

            await _auditWriter.RecordRequiredAsync(new AuditLogEntry
            {
                TenantId = tenant,
                EventType = AuditEventTypes.CatalogPhysicalIndexesChanged,
                ActorSid = actor,
                TargetTable = table.Identifier.ToString(),
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    datasetId = table.Identifier.ToString(),
                    beforeCount,
                    afterCount
                })
            }, ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning(ex, "Audit record failed for non-unique indexes on table {Table}; changes will fail closed.", table.Identifier);
            return false;
        }
    }

    private static object DirectionView(TableMetadata metadata) => new
    {
        primaryKeyDirections = KeyDirections.ToWire(metadata.PrimaryKeyDirections),
        uniqueIndexDirections = metadata.Indexes.Where(static i => i.IsUnique).Select(static i => KeyDirections.ToWire(i.Directions)).ToArray()
    };
}
