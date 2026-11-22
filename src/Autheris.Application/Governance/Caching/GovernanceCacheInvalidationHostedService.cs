using Microsoft.Extensions.Hosting;

namespace Autheris.Application.Governance.Caching;

/// <summary>
/// Subscribes to <see cref="IGovernanceChangeNotifier"/> so every cluster replica drops stale governance caches
/// upon mutation events, falling back to full invalidation on any handler error.
/// </summary>
public sealed class GovernanceCacheInvalidationHostedService(
    IGovernanceChangeNotifier notifier,
    IEnumerable<IGovernanceCacheInvalidator> targets) : IHostedService, IDisposable
{
    private readonly IGovernanceChangeNotifier _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    private readonly IReadOnlyList<IGovernanceCacheInvalidator> _targets = (targets ?? throw new ArgumentNullException(nameof(targets))).ToList();
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription ??= _notifier.Subscribe(OnChange);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private void OnChange(GovernanceChangeEvent change)
    {
        foreach (var target in _targets)
        {
            try
            {
                if (change.IsFullInvalidation)
                {
                    target.InvalidateAll();
                }
                else
                {
                    target.InvalidateTenant(change.TenantId);
                }
            }
            catch
            {
                target.InvalidateAll();
            }
        }
    }
}
