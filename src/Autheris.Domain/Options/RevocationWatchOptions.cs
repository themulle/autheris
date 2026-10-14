namespace Autheris.Domain.Options;

/// <summary>
/// SR-L-18: options of the periodic token revocation re-check on open subscriptions / WebSocket sessions.
/// Bound to the configuration section <see cref="SectionName"/>.
/// </summary>
public sealed class RevocationWatchOptions
{
    public const string SectionName = "Gateway:Subscriptions";

    /// <summary>Default number of consecutive failed checks after which a live session is terminated.</summary>
    public const int DefaultMaxConsecutiveFailures = 3;

    /// <summary>
    /// Number of consecutive revocation check failures (store unavailable or unexpected error) after which the
    /// session is treated as revoked. Must be at least 1.
    /// </summary>
    public int RevocationCheckMaxConsecutiveFailures { get; init; } = DefaultMaxConsecutiveFailures;

    /// <summary>
    /// When true (default) a session whose revocation status cannot be verified is terminated once the threshold is
    /// reached. When false, check failures are only logged (fail open; not recommended for production).
    /// </summary>
    public bool RevocationCheckFailClosed { get; init; } = true;
}
