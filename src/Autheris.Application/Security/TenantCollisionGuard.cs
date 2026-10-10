namespace Autheris.Application.Security;

using Autheris.Domain.Options;

/// <summary>
/// Decision B-1: request-time denial of tenants whose id collides case-insensitively with another configured tenant id.
/// The set is derived from the configured tenant ids (<see cref="TenantCollisionCheck"/>); the match is case-insensitive, so a
/// further spelling of a colliding group is denied as well (fail closed).
/// </summary>
public sealed class TenantCollisionGuard
{
    private readonly HashSet<string> _denied;

    public TenantCollisionGuard(IEnumerable<string?> configuredTenantIds)
    {
        ArgumentNullException.ThrowIfNull(configuredTenantIds);
        _denied = new HashSet<string>(TenantCollisionCheck.DeniedTenants(configuredTenantIds), StringComparer.OrdinalIgnoreCase);
    }

    public static TenantCollisionGuard FromOptions(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TenantCollisionGuard(ConfiguredTenantIds(options));
    }

    public bool HasCollisions => _denied.Count > 0;

    public bool IsDenied(string? tenantId) => !string.IsNullOrWhiteSpace(tenantId) && _denied.Contains(tenantId.Trim());

    /// <summary>Every tenant id that configuration names; the only reliable tenant set at startup and request time.</summary>
    public static IEnumerable<string?> ConfiguredTenantIds(GatewayOptions options)
    {
        var forwardAuth = options.Authentication.ForwardAuth;
        yield return forwardAuth.DefaultTenantId;
        foreach (var id in forwardAuth.AllowedTenantIds) yield return id;
        foreach (var id in options.WebSql.TenantDataSourceAllowlist.Keys) yield return id;
        yield return options.OpenMetadata.DefaultTenantId;
        foreach (var id in options.OpenMetadata.ServiceDatabaseToTenantMap.Values) yield return id;
        foreach (var id in options.Itsm.InstanceToTenantMap.Values) yield return id;
    }
}
