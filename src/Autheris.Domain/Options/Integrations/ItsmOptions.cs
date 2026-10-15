using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Domain.Options;

public sealed class ItsmOptions
{
    public bool Enabled { get; init; }
    public ItsmSystemType DefaultSystem { get; init; } = ItsmSystemType.ServiceNow;
    public string ServiceNowBaseUrl { get; init; } = string.Empty;
    public string ServiceNowUsername { get; init; } = string.Empty;
    public string ServiceNowPassword { get; init; } = string.Empty;
    public string ServiceNowTable { get; init; } = "change_request";
    public string JiraBaseUrl { get; init; } = string.Empty;
    public string JiraEmail { get; init; } = string.Empty;
    public string JiraApiToken { get; init; } = string.Empty;
    public string JiraProjectKey { get; init; } = "SEC";
    public string JiraIssueType { get; init; } = "Task";
    public int RecertificationWarningDays { get; init; } = 3;
    public Dictionary<string, string> InstanceToTenantMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SEC H-06: Webhook signatures are verified with a per-instance secret (<c>itsm:webhook-secret:{instanceId}</c>).
    /// Only when this legacy switch is set, the single global secret <c>itsm:webhook-secret</c> is accepted as fallback
    /// (every ITSM instance can then sign callbacks for every other instance's tenant).
    /// </summary>
    public bool LegacyGlobalWebhookSecret { get; init; } = false;

    public TenantId? GetTenantForInstance(string instanceId)
    {
        if (InstanceToTenantMap.TryGetValue(instanceId, out var tenantStr) && !string.IsNullOrWhiteSpace(tenantStr))
        {
            return new TenantId(tenantStr);
        }
        return null;
    }
}

public sealed class PluginsOptions
{
    public string Directory { get; init; } = "plugins";
    public bool RequireIntegrityManifest { get; init; } = false;
    public Dictionary<string, string> TrustedPluginHashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExtensibilityOptions
{
    public bool Enabled { get; init; } = true;
    public bool EnableBreakGlass { get; init; } = true;
    public bool RequireJustificationForBreakGlass { get; init; } = true;
    // SEC M-06: Break-glass requires an authorized role by default.
    public bool RequireRoleForBreakGlass { get; init; } = true;

    /// <summary>
    /// Roles that may invoke break-glass when <see cref="RequireRoleForBreakGlass"/> is true. When not configured:
    /// BreakGlassOperator, ClusterAdmin, GovernanceAdmin and SecurityAdmin. A configured list replaces that default
    /// entirely, so an operator who restricts the list gets exactly what was configured. Nullable on purpose: the
    /// configuration binder appends to pre-filled lists, so a non-empty default could not be narrowed. An empty JSON
    /// array aborts the start (it would silently apply the default); to permit break-glass for nobody, set
    /// <see cref="EnableBreakGlass"/> to false.
    /// </summary>
    public List<string>? BreakGlassAllowedRoles { get; init; }

    private static readonly string[] DefaultBreakGlassAllowedRoles = ["BreakGlassOperator", "ClusterAdmin", "GovernanceAdmin", "SecurityAdmin"];

    /// <summary>Configured <see cref="BreakGlassAllowedRoles"/> or the default list.</summary>
    public IReadOnlyList<string> GetEffectiveBreakGlassAllowedRoles() => BreakGlassAllowedRoles ?? (IReadOnlyList<string>)DefaultBreakGlassAllowedRoles;

    public string JustificationHeaderName { get; init; } = "X-Access-Justification";
    public string BreakGlassHeaderName { get; init; } = "X-Break-Glass";
}
