using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Common;

namespace Autheris.Application.OpenMetadata.Interfaces;

public interface IOpenMetadataSyncService
{
    /// <param name="restrictToTenant">
    /// SR-L-23: when set, only tables mapped to this tenant are synchronised and only this tenant's sync consents are
    /// reconciled (tenant-scoped administrators). <c>null</c> synchronises every tenant (cluster administrators, background sync).
    /// </param>
    Task<OpenMetadataSyncResult> SyncPermissionsAsync(bool dryRun = false, TenantId? restrictToTenant = null, CancellationToken ct = default);
    Task<bool> HandleWebhookEventAsync(string eventPayload, string? signatureHeader = null, CancellationToken ct = default);
}
