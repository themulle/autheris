namespace Autheris.Application.ResourceGroups;

using System;
using System.Collections.Generic;
using System.Threading;
using Autheris.Domain.Options;

/// <summary>
/// SEC H-07: Caps long-lived connections (WebSocket upgrades, Server-Sent Events) per principal and per tenant.
/// These connections never end on their own and therefore must not occupy the shared resource group slots.
/// </summary>
public sealed class PersistentConnectionLimiter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _perPrincipal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _perTenant = new(StringComparer.OrdinalIgnoreCase);

    public PersistentConnectionLimiter(ResourceGroupsOptions? options)
    {
        var opts = options ?? new ResourceGroupsOptions();
        MaxPerPrincipal = Math.Max(1, opts.MaxPersistentConnectionsPerPrincipal);
        MaxPerTenant = Math.Max(1, opts.MaxPersistentConnectionsPerTenant);
    }

    public int MaxPerPrincipal { get; }
    public int MaxPerTenant { get; }

    /// <summary>
    /// Tries to reserve a connection slot. Returns null when the principal or the tenant limit is exhausted.
    /// The returned handle must be disposed when the connection ends.
    /// </summary>
    public IDisposable? TryAcquire(string principalKey, string tenantKey)
    {
        ArgumentNullException.ThrowIfNull(principalKey);
        ArgumentNullException.ThrowIfNull(tenantKey);

        lock (_sync)
        {
            _perPrincipal.TryGetValue(principalKey, out var principalCount);
            _perTenant.TryGetValue(tenantKey, out var tenantCount);

            if (principalCount >= MaxPerPrincipal || tenantCount >= MaxPerTenant)
            {
                return null;
            }

            _perPrincipal[principalKey] = principalCount + 1;
            _perTenant[tenantKey] = tenantCount + 1;
        }

        return new Handle(this, principalKey, tenantKey);
    }

    public int GetPrincipalCount(string principalKey)
    {
        lock (_sync)
        {
            return _perPrincipal.TryGetValue(principalKey, out var c) ? c : 0;
        }
    }

    public int GetTenantCount(string tenantKey)
    {
        lock (_sync)
        {
            return _perTenant.TryGetValue(tenantKey, out var c) ? c : 0;
        }
    }

    private void Release(string principalKey, string tenantKey)
    {
        lock (_sync)
        {
            Decrement(_perPrincipal, principalKey);
            Decrement(_perTenant, tenantKey);
        }
    }

    private static void Decrement(Dictionary<string, int> map, string key)
    {
        if (map.TryGetValue(key, out var count))
        {
            if (count <= 1)
            {
                map.Remove(key);
            }
            else
            {
                map[key] = count - 1;
            }
        }
    }

    private sealed class Handle : IDisposable
    {
        private PersistentConnectionLimiter? _owner;
        private readonly string _principalKey;
        private readonly string _tenantKey;

        public Handle(PersistentConnectionLimiter owner, string principalKey, string tenantKey)
        {
            _owner = owner;
            _principalKey = principalKey;
            _tenantKey = tenantKey;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(_principalKey, _tenantKey);
        }
    }
}
