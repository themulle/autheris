namespace Autheris.Extensions.Dbt;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;

public sealed partial class DbtMetadataIngestionService
{
    private async Task FlushPendingModelUpdatesAsync(
        IReadOnlyList<(TableMetadata Metadata, IReadOnlyList<string> Tags)> pendingModelUpdates,
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
        await SyncColumnClassificationsAsync(pendingModelUpdates, warnings, ct).ConfigureAwait(false);

        foreach (var (metadata, tags) in pendingModelUpdates)
        {
            await ImportModelTagsAsync(metadata.Identifier, tags, warnings, ct).ConfigureAwait(false);
        }
    }
}
