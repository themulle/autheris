using Autheris.Domain.Common;

namespace Autheris.Application.Catalog.OpenMetadata;

/// <summary>
/// Result of handling a signature-verified OpenMetadata webhook payload.
/// </summary>
public enum OpenMetadataWebhookOutcome
{
    Processed,
    Rejected,
    Unavailable
}

/// <summary>
/// Core port implemented by the OpenMetadata module: the on-demand and scheduled permission sync. Without the module no
/// implementation is registered and callers answer "not available".
/// </summary>
public interface IOpenMetadataSyncService
{
    /// <param name="restrictToTenant">
    /// SR-L-23: when set, only tables mapped to this tenant are synchronised and only this tenant's sync consents are
    /// reconciled (tenant-scoped administrators). <c>null</c> synchronises every tenant (cluster administrators, background sync).
    /// </param>
    Task<OpenMetadataSyncResult> SyncPermissionsAsync(bool dryRun = false, TenantId? restrictToTenant = null, CancellationToken ct = default);
}
