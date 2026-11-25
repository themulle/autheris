using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Model;

namespace Autheris.Domain.Options;

/// <summary>External data catalog synchronization and tag-mapping options (<c>Gateway:Catalog</c>).</summary>
public sealed class DataCatalogOptions
{
    /// <summary>Enables external data catalog synchronization.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Limits of the in-memory catalog caches (<c>Gateway:Catalog:Cache</c>).</summary>
    public CatalogCacheOptions Cache { get; init; } = new();

    /// <summary>External catalog provider implementation to synchronize with.</summary>
    public DataCatalogProviderType Provider { get; init; } = DataCatalogProviderType.OpenMetadata;
    [Range(1, 1440)] public int SyncIntervalMinutes { get; init; } = 60;

    /// <summary>
    /// EXT-7: When true, tables newly discovered in the catalog are automatically marked as Active = true.
    /// Default: false (tables remain inactive until approved by a Data Steward).
    /// </summary>
    public bool ActivateNewTables { get; init; } = false;

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

    /// <summary>Mapping from external catalog tag names to gateway masking rule identifiers.</summary>
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

    /// <summary>Catalog tag names recognized as GDPR Article 9 special-category personal data.</summary>
    public List<string> GdprArticle9Tags { get; init; } =
    [
        "GDPR.Article9", "GDPR.Art9", "Art9", "HealthData", "Biometric",
        "Genetic", "ReligiousBelief", "TradeUnionMembership", "SexLife",
        "SexualOrientation", "PoliticalOpinion", "SpecialCategoryData"
    ];

    /// <summary>Catalog tag names recognized as general personally identifiable information (PII).</summary>
    public List<string> PiiTags { get; init; } =
    [
        "PII", "PersonalData", "Classification.PII", "Email", "Phone",
        "SSN", "NationalId", "CreditCard", "Confidential"
    ];
}

/// <summary>dbt Cloud/manifest integration options (<c>Gateway:Dbt</c>).</summary>
public sealed class DbtOptions
{
    /// <summary>Enables dbt manifest synchronization and webhook handling.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Shared HMAC secret used to verify incoming dbt Cloud webhook signatures.</summary>
    public string WebhookSecret { get; init; } = string.Empty;

    /// <summary>
    /// Issue #282: a dbt sync registers manifest models and sources that have no catalog entry yet, when their database resolves
    /// to a configured SQL data source. false keeps the previous behaviour (update existing entries only).
    /// </summary>
    public bool CreateMissingTables { get; init; } = true;

    /// <summary>
    /// Issue #282: newly registered tables are inactive (invisible) until an administrator activates them. true makes
    /// them live without masks or consents; configuration only, never a request parameter.
    /// </summary>
    public bool ActivateNewTables { get; init; }

    /// <summary>
    /// Issue #282: dbt database name to<c>DataSources.Connections</c> key. A database without an entry resolves to the
    /// connection of the same name (case-insensitive); there is no wildcard and no default connection.
    /// </summary>
    public Dictionary<string, string> DatabaseMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
