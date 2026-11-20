namespace Autheris.Modules.Dbt;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
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

    internal readonly record struct PendingModelUpdate(
        TableMetadata Metadata,
        IReadOnlyList<string> Tags,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? SyntheticSamples,
        bool UpsertMetadata,
        bool SyncClassifications,
        bool ImportTags);

    private async Task FlushPendingModelUpdatesAsync(
        IReadOnlyList<PendingModelUpdate> pendingModelUpdates,
        List<string> warnings,
        CancellationToken ct)
    {
        if (pendingModelUpdates.Count == 0)
        {
            return;
        }

        await using var scope = Autheris.Application.Catalog.CatalogWriteScopeFactoryExtensions.BeginOrNoop(_catalogWriteScopes, "dbt-manifest-sync");

        var upsertBatch = pendingModelUpdates
            .Where(static u => u.UpsertMetadata)
            .Select(static u => u.Metadata)
            .ToList();
        if (upsertBatch.Count > 0)
        {
            await _metadataRepository.UpsertBatchWithFallbackAsync(upsertBatch, ct).ConfigureAwait(false);
        }

        var classificationBatch = pendingModelUpdates
            .Where(static u => u.SyncClassifications)
            .Select(static u => u.Metadata)
            .ToList();
        if (classificationBatch.Count > 0)
        {
            await SyncColumnClassificationsAsync(classificationBatch, warnings, ct).ConfigureAwait(false);
        }

        foreach (var update in pendingModelUpdates)
        {
            if (update.ImportTags)
            {
                await ImportModelTagsAsync(update.Metadata.Identifier, update.Tags, warnings, ct).ConfigureAwait(false);
            }

            if (update.SyntheticSamples is { Count: > 0 })
            {
                await IngestDbtSyntheticSamplesAsync(update.Metadata, update.SyntheticSamples, warnings, ct).ConfigureAwait(false);
            }
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

        if (before is not null && string.Equals(before.ContentSha256, combinedSha, StringComparison.Ordinal))
        {
            return;
        }

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

    private static bool IsTableMetadataUnchanged(TableMetadata existingMeta, TableMetadata updatedMetadata)
    {
        if (existingMeta.Columns.Count != updatedMetadata.Columns.Count)
        {
            return false;
        }

        for (var i = 0; i < existingMeta.Columns.Count; i++)
        {
            if (existingMeta.Columns[i].DiscoveryHidden != updatedMetadata.Columns[i].DiscoveryHidden)
            {
                return false;
            }
        }

        if (!AreFilterGroupsEqual(existingMeta.RequiredFilterColumnGroups, updatedMetadata.RequiredFilterColumnGroups))
        {
            return false;
        }

        try
        {
            var before = CatalogTablePersistedProjection.From(existingMeta);
            var after = CatalogTablePersistedProjection.From(updatedMetadata);
            return CatalogTablePersistedProjection.Compare(before, after).Classification == CatalogWriteClassification.Unchanged;
        }
        catch (InvalidOperationException)
        {
            // Ambiguous or unprojectable state: fall back to writing.
            return false;
        }
    }

    private static bool AreFilterGroupsEqual(
        System.Collections.Immutable.ImmutableArray<System.Collections.Immutable.ImmutableArray<string>> left,
        System.Collections.Immutable.ImmutableArray<System.Collections.Immutable.ImmutableArray<string>> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var lGroup = left[i];
            var rGroup = right[i];
            if (lGroup.Length != rGroup.Length)
            {
                return false;
            }

            for (var j = 0; j < lGroup.Length; j++)
            {
                if (!string.Equals(lGroup[j], rGroup[j], StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
