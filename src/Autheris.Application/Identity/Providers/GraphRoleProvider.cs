namespace Autheris.Application.Identity.Providers;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;

public sealed class GraphRoleProvider : IRoleProvider
{
    public const string Id = "microsoft-graph";
    public string ProviderId => Id;

    private readonly IGraphClientAdapter _adapter;
    private readonly TimeSpan _timeout;
    private readonly int _failureThreshold;
    private readonly TimeSpan _circuitBreakDuration;
    private readonly int _maxGroups;

    private int _consecutiveFailures;
    private DateTimeOffset _circuitBreakerOpenUntil = DateTimeOffset.MinValue;
    private readonly object _lock = new();

    public GraphRoleProvider(
        IGraphClientAdapter adapter,
        TimeSpan? timeout = null,
        int failureThreshold = 5,
        TimeSpan? circuitBreakDuration = null,
        int maxGroups = 1024)
    {
        _maxGroups = maxGroups;
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

            // More groups than the cap (a further page beyond it) is unknown, not a truncated list: a dropped group could carry a deny.
            if (groups.Count > _maxGroups)
            {
                return ProviderRoleResult.FailedResult;
            }

            // Accepted: canonical SIDs (on-premises SID of a synced group) and object-id GUIDs (cloud group, or a synced group
            // without on-premises SID) converted to the cloud SID. Anything else is dropped and makes the answer incomplete.
            var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var incomplete = false;
            foreach (var group in groups)
            {
                if (SidFormat.IsCanonical(group))
                {
                    sids.Add(group);
                }
                else if (Guid.TryParseExact(group, "D", out var objectId) && SidFormat.TryFromEntraObjectId(objectId, out var cloudSid))
                {
                    sids.Add(cloudSid.Value);
                }
                else
                {
                    incomplete = true;
                }
            }

            var result = new ProviderRoleResult(
                sids,
                new HashSet<string>(appRoles, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            return incomplete ? result with { Failed = true } : result;
        }
        catch (GraphThrottledException throttled)
        {
            // 429 Throttle response - record failure and trip circuit for Retry-After duration if large
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
            // Fail-closed on any error or timeout
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
