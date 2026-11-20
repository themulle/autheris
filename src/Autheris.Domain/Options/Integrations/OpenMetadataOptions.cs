using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Common;

namespace Autheris.Domain.Options;

/// <summary>OpenMetadata catalog integration and webhook options (<c>Gateway:OpenMetadata</c>).</summary>
public sealed class OpenMetadataOptions
{
    /// <summary>Enables OpenMetadata catalog synchronization and webhook ingestion.</summary>
    public bool Enabled { get; init; }

    /// <summary>Base URL of the OpenMetadata REST API.</summary>
    public string ServerUrl { get; init; } = "http://localhost:8585/api/v1";

    /// <summary>Bearer token used to authenticate against the OpenMetadata REST API.</summary>
    public string AuthToken { get; init; } = string.Empty;

    /// <summary>Shared HMAC secret used to verify incoming OpenMetadata webhook signatures.</summary>
    public string WebhookSecret { get; init; } = string.Empty;

    /// <summary>Optional OpenMetadata service name filter limiting which services are synchronized.</summary>
    public string ServiceFilter { get; init; } = string.Empty;

    [Range(1, 1440)] public int SyncIntervalMinutes { get; init; } = 30;

    /// <summary>Mapping from OpenMetadata tag names to gateway masking rule identifiers.</summary>
    public Dictionary<string, string> TagToMaskingRuleMap { get; init; } = new()
    {
        ["PII.Sensitive"] = "REDACT",
        ["PII.Email"] = "MASK_EMAIL",
        ["PII.Pseudonym"] = "HMAC_SHA256",
        ["PersonalData.Personal"] = "REDACT"
    };

    /// <summary>Mapping from OpenMetadata team names to gateway group security identifiers (SIDs).</summary>
    public Dictionary<string, string> TeamToGroupSidMap { get; init; } = new();

    /// <summary>Mapping from OpenMetadata user names to gateway user security identifiers (SIDs).</summary>
    public Dictionary<string, string> UserToUserSidMap { get; init; } = new();

    /// <summary>
    /// EX-01: Explicit allowlist/mapping of OpenMetadata role names to Gateway roles.
    /// Only roles present in this map are converted into gateway role consents.
    /// </summary>
    public Dictionary<string, string> RoleToGatewayRoleMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SEC H-19: When true, the OpenMetadata sync automatically creates allow consents from OM policies (ViewAll/ViewSampleData only,
    /// without condition). Default: false - suggestions are only logged.
    /// </summary>
    public bool AutoCreateConsents { get; init; } = false;

    /// <summary>
    /// SEC E-09 / EX-06: Explicit mapping "service.database" (OpenMetadata) -&gt; Gateway domain. When the map is set,
    /// tables without an entry are rejected. Empty: domain = service name (legacy behavior); tables with identical
    /// service.schema.table from different databases are then rejected instead of overwriting each other.
    /// </summary>
    public Dictionary<string, string> ServiceDatabaseToDomainMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SEC E-09 / EX-06: Tables newly imported from OpenMetadata (sync, webhook, catalog provider OpenMetadata) are
    /// active. Default: false - new tables are created inactive until a gateway administrator enables them.
    /// </summary>
    public bool ActivateNewTables { get; init; } = false;

    /// <summary>
    /// EX-03: Default tenant assigned to OpenMetadata imported consents and catalog entries.
    /// Default: none (entries without a mapping use Gateway:Tenancy:DefaultTenantId, or are skipped when none is configured).
    /// </summary>
    public string? DefaultTenantId { get; init; }

    /// <summary>
    /// EX-03: Explicit mapping "service.database" or "service" (OpenMetadata) -&gt; Gateway tenant.
    /// When the map is set, tables are assigned to the tenant primarily via this mapping.
    /// </summary>
    public Dictionary<string, string> ServiceDatabaseToTenantMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
