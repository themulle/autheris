namespace Autheris.Modules.OpenMetadata;

using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed partial class OpenMetadataSyncService
{
    private async Task FlushBatchUpsertsAsync(
        IReadOnlyList<(string FullyQualifiedName, TableMetadata Metadata)> pendingUpserts,
        List<string> warnings,
        CancellationToken ct)
    {
        if (pendingUpserts.Count == 0)
        {
            return;
        }

        await using var scope = Autheris.Application.Catalog.CatalogWriteScopeFactoryExtensions.BeginOrNoop(_catalogWriteScopes, "openmetadata-sync");
        var batch = pendingUpserts.Select(static p => p.Metadata).ToList();
        try
        {
            await _metadataRepo.UpsertBatchWithFallbackAsync(batch, ct).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Fall back to per-table upserts so one invalid table produces its per-table warning without dropping the remaining tables.
        }

        foreach (var (fqn, metadata) in pendingUpserts)
        {
            try
            {
                await _metadataRepo.UpsertTableMetadataAsync(metadata, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var warning = $"Failed to map or upsert table {fqn}: {ex.Message}";
                _logger.LogWarning(ex, "{Warning}", warning);
                warnings.Add(warning);
            }
        }
    }
}
