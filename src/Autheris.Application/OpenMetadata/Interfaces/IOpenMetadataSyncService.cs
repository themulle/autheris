using Autheris.Application.OpenMetadata.Models;

namespace Autheris.Application.OpenMetadata.Interfaces;

public interface IOpenMetadataSyncService
{
    Task<OpenMetadataSyncResult> SyncPermissionsAsync(bool dryRun = false, CancellationToken ct = default);
    Task<bool> HandleWebhookEventAsync(string eventPayload, string? signatureHeader = null, CancellationToken ct = default);
}
