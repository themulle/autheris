using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Model;

namespace Autheris.Domain.Options;

public sealed class PurviewOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string AccountName { get; init; } = string.Empty;
}

public sealed class CollibraOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiToken { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? CommunityId { get; init; }
}

public sealed class AlationOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiToken { get; init; } = string.Empty;
    public int CustomFieldIdPii { get; init; } = 1001;

    /// <summary>
    /// SEC E-08: Explicit mapping of Alation data source (<c>ds_id</c>, numeric) -&gt; Gateway domain.
    /// Tables of unmapped data sources are discarded (no ghost tables).
    /// </summary>
    public Dictionary<string, string> DataSourceToDomainMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SEC E-08: Development only: <see cref="ApiToken"/> may be a plaintext token when it cannot be resolved via the
    /// secret provider. Default: false (fail-closed in all environments).
    /// </summary>
    public bool AllowPlaintextApiTokenInDevelopment { get; init; } = false;
}

public sealed class DataCatalogOptions
{
    public bool Enabled { get; init; } = false;
    public DataCatalogProviderType Provider { get; init; } = DataCatalogProviderType.OpenMetadata;
    public DataCatalogSyncMode SyncMode { get; init; } = DataCatalogSyncMode.Mirror;
    [Range(1, 1440)] public int SyncIntervalMinutes { get; init; } = 60;
    public string WebhookSecret { get; init; } = string.Empty;

    /// <summary>
    /// EXT-7: When true, tables newly discovered in the catalog are automatically marked as Active = true.
    /// Default: false (tables remain inactive until approved by a Data Steward).
    /// </summary>
    public bool ActivateNewTables { get; init; } = false;

    /// <summary>
    /// Open Schema Mode: When true, all users (even without GovernanceAdmin/CatalogReader roles)
    /// may view the entire data catalog, OpenAPI specifications, indexes and table schemas.
    /// Default: false (Disabled - Zero-Trust role enforcement active).
    /// </summary>
    public bool OpenSchema { get; init; } = false;

    /// <summary>
    /// When true, all authenticated users can view the complete metadata catalog
    /// (for data discovery and access requests). Data access itself remains strictly protected by consents.
    /// Default: false (Zero-Trust: users only see tables in the catalog for which they hold consents).
    /// </summary>
    public bool AllowAuthenticatedCatalogDiscovery { get; init; } = false;

    /// <summary>
    /// SR-L-21: Tables (<c>domain.schema.table</c>) visible in the GraphQL catalog to every tenant's administrators and in
    /// discovery mode, in addition to the tables on which the tenant holds a consent. Only relevant in multi-tenant deployments.
    /// Default: empty (a tenant sees only tables it has a consent record on).
    /// </summary>
    public IReadOnlyList<string> SharedTables { get; init; } = [];

    /// <summary>Issue #111: maximum number of catalog tags per dataset (all sources together). Valid range 1-256, default 32.</summary>
    public int MaxTagsPerDataset { get; init; } = 32;

    /// <summary>
    /// Issue #111: catalog visibility given to tables created after this setting is applied (never at read time, existing tables never change).
    /// Only <see cref="DatasetVisibility.Hidden"/> and <see cref="DatasetVisibility.Summary"/> are valid; <see cref="DatasetVisibility.Full"/> fails start-up validation.
    /// </summary>
    public DatasetVisibility DefaultVisibility { get; init; } = DatasetVisibility.Hidden;

    public PurviewOptions Purview { get; init; } = new();
    public CollibraOptions Collibra { get; init; } = new();
    public AlationOptions Alation { get; init; } = new();

    public string OpenLineageEndpoint { get; init; } = "http://localhost:5000/api/v1/lineage";
    public string OpenLineageApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Precedence order of documentation sources from highest to lowest priority.
    /// Sources with a lower rank cannot overwrite existing descriptions from higher-ranked sources.
    /// </summary>
    public List<string> DocumentationSourcePrecedence { get; init; } =
    [
        "Manual",
        "DataCatalog",
        "dbt",
        "OpenApi",
        "Database",
        "Default"
    ];

    public Dictionary<string, string> TagToMaskingRuleMap { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PII.Sensitive"] = "REDACT",
        ["PII.Email"] = "MASK_EMAIL",
        ["PII.Pseudonym"] = "HMAC_SHA256",
        ["PersonalData.Personal"] = "REDACT",
        ["Classification.PII"] = "REDACT",
        ["Classification.Email"] = "MASK_EMAIL",
        ["Confidential"] = "REDACT",
        ["Restricted"] = "REDACT"
    };

    public List<string> GdprArticle9Tags { get; init; } =
    [
        "GDPR.Article9", "GDPR.Art9", "Art9", "HealthData", "Biometric",
        "Genetic", "ReligiousBelief", "TradeUnionMembership", "SexLife",
        "SexualOrientation", "PoliticalOpinion", "SpecialCategoryData"
    ];

    public List<string> PiiTags { get; init; } =
    [
        "PII", "PersonalData", "Classification.PII", "Email", "Phone",
        "SSN", "NationalId", "CreditCard", "Confidential"
    ];
}

public sealed class DbtOptions
{
    public bool Enabled { get; init; } = true;
    public string WebhookSecret { get; init; } = string.Empty;

    /// <summary>
    /// Issue #282: a dbt sync registers manifest models and sources that have no catalog entry yet, when their database resolves
    /// to a configured SQL data source. false keeps the previous behaviour (update existing entries only).
    /// </summary>
    public bool CreateMissingTables { get; init; } = true;

    /// <summary>
    /// Issue #282 (SEC-282-2): newly registered tables are inactive (invisible) until an administrator activates them. true makes
    /// them live without masks or consents; configuration only, never a request parameter.
    /// </summary>
    public bool ActivateNewTables { get; init; }

    /// <summary>
    /// Issue #282 (SEC-282-3): dbt database name to <c>DataSources.Connections</c> key. A database without an entry resolves to the
    /// connection of the same name (case-insensitive); there is no wildcard and no default connection.
    /// </summary>
    public Dictionary<string, string> DatabaseMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Issue #393 (part b): the dbt governance import (<c>POST /api/extensions/dbt/governance</c>) may write table data owners, who then
    /// act as consent approvers. false (default) rejects any payload that carries <c>owner</c> / <c>owners</c>. Configuration only,
    /// never a request parameter; the manifest sync never writes owners.
    /// </summary>
    public bool ImportDataOwners { get; init; }
}

public sealed class BackstageIntegrationOptions
{
    public bool Enabled { get; init; } = true;
    public string DefaultOwner { get; init; } = "group:default/data-stewards";
    public string DefaultSystem { get; init; } = "enterprise-data-mesh";
    public string DefaultNamespace { get; init; } = "default";
    public bool IncludeTablesAsApis { get; init; } = true;
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Absolute URL of a GraphQL explorer hosted by the portal itself (e.g. the Backstage page that proxies the
    /// gateway with credentials and the CSRF header). When set, it replaces the Banana Cake Pop link in the exported
    /// catalog entity; empty keeps the link to the embedded Banana Cake Pop UI.
    /// </summary>
    public string GraphQlExplorerUrl { get; init; } = string.Empty;
}
