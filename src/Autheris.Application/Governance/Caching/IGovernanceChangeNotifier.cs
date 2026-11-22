namespace Autheris.Application.Governance.Caching;

/// <summary>Notification describing a committed tenant governance or identity mutation, or a full cache invalidation signal.</summary>
public sealed record GovernanceChangeEvent(
    string TenantId,
    long Epoch,
    Guid? TableId,
    DateTimeOffset OccurredAtUtc,
    string? SubjectKey = null,
    string? GroupId = null,
    string? RealmId = null)
{
    public bool IsFullInvalidation => string.IsNullOrEmpty(TenantId);

    public static GovernanceChangeEvent All(DateTimeOffset occurredAtUtc) =>
        new(string.Empty, 0, null, occurredAtUtc);

    public static GovernanceChangeEvent ForIdentity(
        string tenantId,
        string realmId,
        DateTimeOffset occurredAtUtc,
        string? subjectKey = null,
        string? groupId = null) =>
        new(tenantId, 0, null, occurredAtUtc, subjectKey, groupId, realmId);
}

/// <summary>Broadcasts governance mutation notifications locally and across cluster replicas.</summary>
public interface IGovernanceChangeNotifier
{
    Task NotifyAsync(GovernanceChangeEvent change, CancellationToken ct = default);
    IDisposable Subscribe(Action<GovernanceChangeEvent> handler);
}

/// <summary>Target cache capable of tenant-scoped or full invalidation.</summary>
public interface IGovernanceCacheInvalidator
{
    void InvalidateTenant(string tenantId);
    void InvalidateAll();
}
