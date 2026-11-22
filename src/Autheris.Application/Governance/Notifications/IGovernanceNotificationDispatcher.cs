namespace Autheris.Application.Governance.Notifications;

/// <summary>
/// Outbound governance notification payload emitted on security-sensitive governance or identity events.
/// </summary>
public sealed record GovernanceNotificationMessage(
    string EventType,
    string TenantId,
    string RealmId,
    string SubjectOrGroupId,
    string Summary,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Dispatches outbound governance notifications (e.g. webhooks). Default DI registration is a no-op until webhook delivery is configured.
/// </summary>
public interface IGovernanceNotificationDispatcher
{
    Task DispatchAsync(GovernanceNotificationMessage message, CancellationToken ct = default);
}

/// <summary>
/// Default no-op <see cref="IGovernanceNotificationDispatcher"/> used when external notification dispatch is disabled or not yet wired.
/// </summary>
public sealed class NoOpGovernanceNotificationDispatcher : IGovernanceNotificationDispatcher
{
    public static readonly NoOpGovernanceNotificationDispatcher Instance = new();

    public Task DispatchAsync(GovernanceNotificationMessage message, CancellationToken ct = default) => Task.CompletedTask;
}
