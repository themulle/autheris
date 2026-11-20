namespace Autheris.Modules.Dbt;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;

public sealed partial class DbtMetadataIngestionService
{
    private const string DbtIngestionActor = "dbt-ingestion";

    private async Task FlushPendingModelUpdatesAsync(
        IReadOnlyList<(TableMetadata Metadata, IReadOnlyList<string> Tags, IReadOnlyList<IReadOnlyDictionary<string, object?>>? SyntheticSamples)> pendingModelUpdates,
        List<string> warnings,
        CancellationToken ct)
    {
        if (pendingModelUpdates.Count == 0)
        {
            return;
        }

        await using var scope = Autheris.Application.Catalog.CatalogWriteScopeFactoryExtensions.BeginOrNoop(_catalogWriteScopes, "dbt-manifest-sync");
        var batch = pendingModelUpdates.Select(static u => u.Metadata).ToList();
        await _metadataRepository.UpsertBatchWithFallbackAsync(batch, ct).ConfigureAwait(false);
        await SyncColumnClassificationsAsync(batch, warnings, ct).ConfigureAwait(false);

        foreach (var (metadata, tags, syntheticSamples) in pendingModelUpdates)
        {
            await ImportModelTagsAsync(metadata.Identifier, tags, warnings, ct).ConfigureAwait(false);
            await IngestDbtSyntheticSamplesAsync(metadata, syntheticSamples, warnings, ct).ConfigureAwait(false);
        }
    }

    private async Task IngestDbtSyntheticSamplesAsync(
        TableMetadata metadata,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? samples,
        List<string> warnings,
        CancellationToken ct)
    {
        if (samples is null || samples.Count == 0 || _artifactStore is null)
        {
            return;
        }

        var maxRows = _discoveryOptions?.Value.Discovery.MaxSampleRows ?? new CatalogDiscoverySettings().MaxSampleRows;
        var validation = DiscoveryArtifactValidator.ValidateSampleRows(samples, metadata, maxRows);
        if (!validation.IsValid)
        {
            _logger.LogWarning("Table {Table}: dbt synthetic_samples rejected ({Code}): {Message}", metadata.Identifier, validation.ViolationCode, validation.Message);
            warnings.Add($"Table '{metadata.Identifier}': dbt synthetic_samples rejected ({validation.ViolationCode}): {validation.Message}");
            return;
        }

        var before = await _artifactStore.GetAsync(metadata.Table.Id, ct).ConfigureAwait(false);
        var combinedSha = DiscoveryArtifactValidator.ComputeContentSha256(
            DiscoveryArtifactValidator.SupportedSchemaVersion,
            validation.SamplesJson,
            before?.StatisticsJson);

        var resolvedTenant = !string.IsNullOrWhiteSpace(metadata.OwnerTenantId) &&
                             TenantId.TryParse(metadata.OwnerTenantId, out var parsedOwner)
            ? parsedOwner
            : TenantResolver.EffectiveDefaultOf(_gatewayOptions?.Value.Tenancy);
        if (resolvedTenant is not { } parsedTenant)
        {
            _logger.LogWarning("Table {Table}: dbt synthetic_samples skipped (TENANT_UNRESOLVED).", metadata.Identifier);
            warnings.Add($"Table '{metadata.Identifier}': dbt synthetic_samples skipped (TENANT_UNRESOLVED).");
            return;
        }

        var tenantId = parsedTenant.Value;
        var actor = new Sid(DbtIngestionActor);
        var updated = new DiscoveryArtifacts(
            TableId: metadata.Table.Id,
            TenantId: tenantId,
            SamplesJson: validation.SamplesJson,
            StatisticsJson: before?.StatisticsJson,
            ContentSha256: combinedSha,
            UploadedBy: DbtIngestionActor,
            UploadedAt: _timeProvider.GetUtcNow(),
            SchemaVersion: DiscoveryArtifactValidator.SupportedSchemaVersion);

        await _artifactStore.UpsertAsync(updated, actor, ct).ConfigureAwait(false);

        try
        {
            var entry = new AuditLogEntry
            {
                TenantId = parsedTenant,
                OccurredAt = _timeProvider.GetUtcNow(),
                ActorSid = actor,
                EventType = AuditEventTypes.CatalogDiscoveryArtifactsChanged,
                TargetTable = metadata.Identifier.ToString(),
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    operation = "upsert",
                    source = DbtIngestionActor,
                    beforeSha256 = before?.ContentSha256,
                    afterSha256 = updated.ContentSha256,
                    sampleRowCount = validation.SampleRowCount,
                    statisticsColumnCount = 0
                })
            };

            if (_auditWriter != null)
            {
                await _auditWriter.RecordAsync(entry, ct).ConfigureAwait(false);
            }
            else if (_auditLog != null)
            {
                await _auditLog.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            try
            {
                if (before is null)
                {
                    await _artifactStore.DeleteAsync(metadata.Table.Id, actor, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _artifactStore.UpsertAsync(before, actor, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Keep original audit failure intact.
            }

            throw;
        }
    }
}
