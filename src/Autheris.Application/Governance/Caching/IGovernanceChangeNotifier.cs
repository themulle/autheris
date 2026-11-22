namespace Autheris.Application.Governance.Caching;

/// <summary>Notification describing a committed tenant governance mutation or a full cache invalidation signal.</summary>
public sealed record GovernanceChangeEvent(
    string TenantId,
    long Epoch,
    Guid? TableId,
    DateTimeOffset OccurredAtUtc)
{
    public bool IsFullInvalidation => string.IsNullOrEmpty(TenantId);

    public static GovernanceChangeEvent All(DateTimeOffset occurredAtUtc) =>
        new(string.Empty, 0, null, occurredAtUtc);
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
