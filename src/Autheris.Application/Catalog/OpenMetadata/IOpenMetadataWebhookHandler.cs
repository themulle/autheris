namespace Autheris.Application.Catalog.OpenMetadata;

/// <summary>
/// Core port implemented by the OpenMetadata module. The core webhook route calls it only after the HMAC signature of the
/// payload has been verified, so implementations never verify signatures themselves.
/// </summary>
public interface IOpenMetadataWebhookHandler
{
    /// <returns><c>true</c> when the event was accepted and processed; <c>false</c> when it was rejected (format, replay, window) or failed.</returns>
    Task<bool> HandleVerifiedAsync(string payload, CancellationToken ct);
}
