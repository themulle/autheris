namespace Autheris.Application.Catalog.OpenMetadata;

/// <summary>
/// Core port implemented by the OpenMetadata module. The core webhook route calls it only after the HMAC signature of the
/// payload has been verified, so implementations never verify signatures themselves.
/// </summary>
public interface IOpenMetadataWebhookHandler
{
    /// <returns>Outcome indicating whether the event was processed, rejected (format, replay, window), or failed closed because the replay store is unavailable.</returns>
    Task<OpenMetadataWebhookOutcome> HandleVerifiedAsync(string payload, CancellationToken ct);
}
