namespace Autheris.Application.DataCatalog.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Result of processing a Data Catalog real-time change event webhook.
/// </summary>
public sealed record CatalogWebhookResult(
    bool Success,
    string Status,
    IReadOnlyList<TableIdentifier> AffectedTables,
    string? ErrorMessage = null);

/// <summary>
/// Handler for incoming real-time change and invalidation webhooks from Enterprise Data Catalogs
/// (OpenMetadata, Microsoft Purview, Collibra, Alation).
/// Invalidates distributed L1/L2 caches and increments policy epochs on metadata/classification changes.
/// </summary>
public interface IDataCatalogWebhookHandler
{
    Task<CatalogWebhookResult> HandleWebhookAsync(
        string rawPayload,
        string? signature,
        DateTimeOffset? timestamp,
        string? provider = null,
        CancellationToken ct = default);
}
