namespace Autheris.Application.Identity.Providers;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Identity;

public sealed class StaticMappingRoleProvider : IRoleProvider
{
    public const string Id = "static-mapping";
    public string ProviderId => Id;

    private readonly ConcurrentDictionary<string, HashSet<string>> _userGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _userRoles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _userAttributes = new(StringComparer.OrdinalIgnoreCase);

    public void AddMapping(string userKey, IEnumerable<string>? groups = null, IEnumerable<string>? roles = null, IDictionary<string, string>? attributes = null)
    {
        if (groups != null)
        {
            var set = _userGroups.GetOrAdd(userKey, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            foreach (var g in groups) set.Add(g);
        }

        if (roles != null)
        {
            var set = _userRoles.GetOrAdd(userKey, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            foreach (var r in roles) set.Add(r);
        }

        if (attributes != null)
        {
            var dict = _userAttributes.GetOrAdd(userKey, _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            foreach (var (k, v) in attributes) dict[k] = v;
        }
    }

    public Task<ProviderRoleResult> GetRolesAsync(
        ResolvedIdentity identity,
        IdentityRealm realm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        _userGroups.TryGetValue(identity.UserKey, out var groups);
        _userRoles.TryGetValue(identity.UserKey, out var roles);
        _userAttributes.TryGetValue(identity.UserKey, out var attributes);

        return Task.FromResult(new ProviderRoleResult(
            groups != null ? new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            roles != null ? new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            attributes != null ? new Dictionary<string, string>(attributes, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }
}
