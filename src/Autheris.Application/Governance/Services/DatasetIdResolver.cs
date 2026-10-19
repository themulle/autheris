namespace Autheris.Application.Governance.Services;

using Autheris.Application.Interfaces;
using Autheris.Domain.Common;

/// <summary>
/// Resolves an active dataset by table id (strict "D" GUID) or by <c>domain.schema.table</c> without loading the whole catalog.
/// Shared by <c>PUT /datasets/{id}/keys</c> (issue #94) and the row filter strategy endpoints (issue #107, SEC-RF107-14a), so both use
/// the same resolution. Unknown and inactive datasets are indistinguishable (SEC-94-15).
/// </summary>
public static class DatasetIdResolver
{
    public const int MaxDatasetIdLength = 512;

    public static async Task<TableIdentifier?> ResolveActiveAsync(string? id, ITableMetadataRepository metadataRepository, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadataRepository);
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxDatasetIdLength)
        {
            return null;
        }

        TableIdentifier identifier;
        if (Guid.TryParseExact(id, "D", out var tableGuid))
        {
            var resolved = await metadataRepository.GetActiveTableIdentifierByIdAsync(tableGuid, ct).ConfigureAwait(false);
            if (resolved is not { } found)
            {
                return null;
            }

            identifier = found;
        }
        else if (!TableIdentifier.TryParse(id.Replace('/', '.'), out identifier))
        {
            return null;
        }

        // Catalog row only: the stored key definition is neither loaded nor validated, so an invalid definition can be replaced.
        return await metadataRepository.GetActiveTableIdentifierAsync(identifier, ct).ConfigureAwait(false);
    }
}
