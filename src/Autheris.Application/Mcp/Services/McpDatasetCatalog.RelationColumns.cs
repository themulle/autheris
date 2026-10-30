namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Model;

public sealed partial class McpDatasetCatalog
{
    /// <summary>
    /// Columns of a relation target that the caller may see (Full shape only; the visibility engine already drops denied columns).
    /// Null when the target is not visible or only a summary (I392-SC-05). The bypass path reads the metadata repository, as elsewhere.
    /// </summary>
    private async ValueTask<IReadOnlySet<string>?> VisibleTargetColumnsAsync(ClaimsPrincipal principal, TableIdentifier target, CancellationToken ct)
    {
        TableMetadata? table;
        if (IsBypassed)
        {
            var found = await _metadataRepository.GetTableMetadataAsync(target, ct).ConfigureAwait(false);
            table = found is { Table.IsActive: true } && Shape(true, found) == CatalogOutputShape.Full ? found : null;
        }
        else if (visibility == null)
        {
            return null;
        }
        else
        {
            var row = await visibility.FindVisibleAsync(CallerOf(principal), target, ct).ConfigureAwait(false);
            table = row is { Shape: CatalogOutputShape.Full } full ? full.Table : null;
        }

        return table?.Columns.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
