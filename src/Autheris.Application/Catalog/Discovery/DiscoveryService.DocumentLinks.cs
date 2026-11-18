namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed partial class DiscoveryService
{
    /// <summary>Linked documents are optional enrichment: a failure leaves the objects without documents (fail closed).</summary>
    private async Task<IReadOnlyDictionary<TableIdentifier, Autheris.Application.Docs.Services.DocLinkView>> LoadDocumentLinksAsync(
        string tenantId, IReadOnlyList<TableMetadata> tables, CancellationToken ct)
    {
        if (_docLinks is null)
        {
            return new Dictionary<TableIdentifier, Autheris.Application.Docs.Services.DocLinkView>();
        }

        try
        {
            return await _docLinks.ForTablesAsync(tenantId, tables, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure(ex, "Reading linked documents failed; discovery objects are returned without documents.");
            return new Dictionary<TableIdentifier, Autheris.Application.Docs.Services.DocLinkView>();
        }
    }
}
