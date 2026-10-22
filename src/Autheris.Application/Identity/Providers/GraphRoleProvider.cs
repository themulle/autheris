namespace Autheris.Application.Identity.Providers;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Identity;

public sealed class GraphRoleProvider : IRoleProvider
{
    public const string Id = "microsoft-graph";
    public string ProviderId => Id;

    private readonly IGraphClientAdapter _adapter;
    private readonly TimeSpan _timeout;
    private readonly int _failureThreshold;
    private readonly TimeSpan _circuitBreakDuration;

    private int _consecutiveFailures;
    private DateTimeOffset _circuitBreakerOpenUntil = DateTimeOffset.MinValue;
    private readonly object _lock = new();

    public GraphRoleProvider(
        IGraphClientAdapter adapter,
        TimeSpan? timeout = null,
        int failureThreshold = 5,
        TimeSpan? circuitBreakDuration = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
        _failureThreshold = failureThreshold;
        _circuitBreakDuration = circuitBreakDuration ?? TimeSpan.FromSeconds(30);
    }

    public bool IsCircuitBreakerOpen
    {
        get
        {
            lock (_lock)
            {
                return DateTimeOffset.UtcNow < _circuitBreakerOpenUntil;
            }
        }
    }

    public int ConsecutiveFailures
    {
        get
        {
            lock (_lock)
            {
                return _consecutiveFailures;
            }
        }
    }

    public async Task<ProviderRoleResult> GetRolesAsync(
        ResolvedIdentity identity,
        IdentityRealm realm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_lock)
        {
            if (DateTimeOffset.UtcNow < _circuitBreakerOpenUntil)
            {
                // Circuit is open - fail closed
                return ProviderRoleResult.FailedResult;
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            var groups = await _adapter.GetTransitiveMemberOfAsync(identity.UserKey, cts.Token).ConfigureAwait(false);
            var appRoles = await _adapter.GetAppRoleAssignmentsAsync(identity.UserKey, cts.Token).ConfigureAwait(false);

            lock (_lock)
            {
                _consecutiveFailures = 0;
            }

            return new ProviderRoleResult(
                new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(appRoles, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (GraphThrottledException throttled)
        {
            // SEC-SC-24: 429 Throttle response - record failure and trip circuit for Retry-After duration if large
            lock (_lock)
            {
                _consecutiveFailures++;
                var backoff = throttled.RetryAfter > TimeSpan.Zero ? throttled.RetryAfter : _circuitBreakDuration;
                if (_consecutiveFailures >= _failureThreshold)
                {
                    _circuitBreakerOpenUntil = DateTimeOffset.UtcNow.Add(backoff);
                }
            }
            return ProviderRoleResult.FailedResult;
        }
        catch (Exception)
        {
            // SEC-SC-12 / SEC-SC-24: Fail-closed on any error or timeout
            lock (_lock)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= _failureThreshold)
                {
                    _circuitBreakerOpenUntil = DateTimeOffset.UtcNow.Add(_circuitBreakDuration);
                }
            }
            return ProviderRoleResult.FailedResult;
        }
    }

    public void ResetCircuitBreaker()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _circuitBreakerOpenUntil = DateTimeOffset.MinValue;
        }
    }
}
