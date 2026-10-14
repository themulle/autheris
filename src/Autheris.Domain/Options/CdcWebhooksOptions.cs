namespace Autheris.Domain.Options;

/// <summary>
/// Feature flag for the CDC webhook subscription endpoints (<c>/api/v1/cdc/subscriptions</c>).
/// Disabled by default: while disabled the endpoints answer 501 Not Implemented, no subscription store is registered
/// and no outbound dispatcher HTTP client is configured.
/// Bound to the configuration section <see cref="SectionName"/>.
/// </summary>
public sealed class CdcWebhooksOptions
{
    public const string SectionName = "Gateway:CdcWebhooks";

    /// <summary>Enables the CDC webhook subscription endpoints. Default: <c>false</c>.</summary>
    public bool Enabled { get; init; } = false;
}
