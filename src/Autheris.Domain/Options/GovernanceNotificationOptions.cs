using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>
/// Outbound payload format supported by Data Governance webhook notifications.
/// </summary>
public enum GovernanceNotificationFormat
{
    AdaptiveCard = 0,
    Json = 1
}

/// <summary>
/// Configuration for Data Governance outbound webhook notifications
/// (<c>Gateway:Governance:Notifications</c>).
/// </summary>
public sealed class GovernanceNotificationOptions
{
    public const string SectionName = GatewaySections.GovernanceNotifications;

    /// <summary>
    /// Webhook endpoint configuration for Data Governance notifications.
    /// </summary>
    public GovernanceNotificationWebhookSettings Webhook { get; init; } = new();

    /// <summary>
    /// Outbound webhook delivery timeout in seconds (1 to 60, default 10).
    /// </summary>
    [Range(1, 60)]
    public int TimeoutSeconds { get; init; } = 10;
}

/// <summary>
/// Webhook destination and payload format settings nested under
/// <c>Gateway:Governance:Notifications:Webhook</c>.
/// </summary>
public sealed class GovernanceNotificationWebhookSettings
{
    /// <summary>
    /// Destination HTTPS webhook URL for Data Governance notifications. Treated as a secret; empty disables notifications.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Payload format emitted by the notification dispatcher (<c>AdaptiveCard</c> or <c>Json</c>).
    /// </summary>
    public GovernanceNotificationFormat Format { get; set; } = GovernanceNotificationFormat.AdaptiveCard;
}
