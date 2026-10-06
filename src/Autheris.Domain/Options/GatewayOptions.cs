using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Domain.Options;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    [Required] public HighAvailabilityOptions HighAvailability { get; init; } = new();
    [Required] public AuthenticationOptions Authentication { get; init; } = new();
    [Required] public GovernanceDbOptions GovernanceDb { get; init; } = new();
    [Required] public CachingOptions Caching { get; init; } = new();
    [Required] public RateLimitingOptions RateLimiting { get; init; } = new();
    [Required] public GraphQLOptions GraphQL { get; init; } = new();
    [Required] public DataMaskingOptions DataMasking { get; init; } = new();
    [Required] public AuditOptions Audit { get; init; } = new();
    [Required] public ReverseProxyOptions ReverseProxy { get; init; } = new();
    [Required] public OpenMetadataOptions OpenMetadata { get; init; } = new();
    [Required] public PluginsOptions Plugins { get; init; } = new();
    [Required] public SqlDataSourceOptions DataSources { get; init; } = new();
    [Required] public ItsmOptions Itsm { get; init; } = new();
    [Required] public DataCatalogOptions Catalog { get; init; } = new();
    [Required] public McpOptions Mcp { get; init; } = new();
    [Required] public LakehouseOptions Lakehouse { get; init; } = new();
    [Required] public FederationOptions Federation { get; init; } = new();
    [Required] public ExtensibilityOptions Extensibility { get; init; } = new();
    [Required] public CasbinOptions Casbin { get; init; } = new();
    [Required] public DbtOptions Dbt { get; init; } = new();
    [Required] public BackstageIntegrationOptions Backstage { get; init; } = new();
    [Required] public ResourceGroupsOptions ResourceGroups { get; init; } = new();
    [Required] public SystemMetricsOptions SystemMetrics { get; init; } = new();
    [Required] public GoldenQueryOptions GoldenQueries { get; init; } = new();
    [Required] public ParquetEgressOptions ParquetEgress { get; init; } = new();
    [Required] public OutboundEgressOptions Egress { get; init; } = new();
    [Required] public HitLStepUpOptions HitLStepUp { get; init; } = new();
    [Required] public SingleQueryPushdownOptions SingleQueryPushdown { get; init; } = new();
    [Required] public WebSqlOptions WebSql { get; init; } = new();
    [Required] public SqlEndpointsOptions SqlEndpoints { get; init; } = new();
    [Required] public InsecureGettingStartedOptions Insecure { get; init; } = new();
    [Required] public DevOptions Dev { get; init; } = new();
    [Required] public MssqlChangeTrackingOptions MssqlChangeTracking { get; init; } = new();
    [Required] public PostgreSqlCdcOptions PostgreSqlCdc { get; init; } = new();
    [Required] public TrafficShadowingOptions TrafficShadowing { get; init; } = new();
    [Required] public FinOpsOptions FinOps { get; init; } = new();
    [Required] public SchemaContractsOptions SchemaContracts { get; init; } = new();
    [Required] public IncrementalDeliveryOptions IncrementalDelivery { get; init; } = new();
    [Required] public RebacOptions Rebac { get; init; } = new();
    [Required] public ArrowExportOptions Arrow { get; init; } = new();
    [Required] public DuckDbOlapOptions DuckDbOlap { get; init; } = new();

    /// <summary>
    /// Getting Started Preset Profile: "Strict" (Default) or "Quickstart".
    /// When set to "Quickstart", relaxes catalog discovery, enables introspection and permissive defaults for local development.
    /// </summary>
    public string Profile { get; init; } = "Strict";

    /// <summary>
    /// True for <c>Dev:Preset = Quickstart</c> or the legacy <c>Profile = Quickstart</c> alias. The relaxations of the
    /// preset are expanded into the concrete <c>Insecure.*</c> values at configuration time (DevConfiguration), so the
    /// accessors below do not need to know about it.
    /// </summary>
    public bool IsQuickstartProfile =>
        string.Equals(Profile, "Quickstart", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Dev.Preset, DevOptions.PresetQuickstart, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Open Schema Mode: Allows anyone to view the entire data catalog, OpenAPI specs, and schema documentation.
    /// Default: false (Disabled).
    /// </summary>
    public bool OpenSchema { get; init; } = false;

    /// <summary>
    /// SEC C-04: Explicit opt-in to run the Development environment inside a container
    /// (DOTNET_RUNNING_IN_CONTAINER=true). Alternatively set the environment variable AUTHERIS_ALLOW_DEV_IN_CONTAINER=true.
    /// Without opt-in, startup is aborted, because Development disables most protections.
    /// </summary>
    public bool AllowDevelopmentInContainer { get; init; } = false;

    /// <summary>
    /// SEC M-01: Kestrel request/connection limits.
    /// </summary>
    [Required] public HostingLimitsOptions Hosting { get; init; } = new();

    // Convenience accessors combining global 'Insecure' section and domain-specific options
    public bool IsOpenSchemaAllowed => OpenSchema || Catalog.OpenSchema;
    public bool IsAnonymousAccessAllowed => Insecure.danger_allow_anonymous_access;
    public bool IsConsentBypassed => Insecure.danger_bypass_consent_checks;
    public bool IsColumnMaskingDisabled => Insecure.danger_disable_column_masking;
    public bool IsInsecureTransportAllowed => Insecure.danger_allow_insecure_transport;
    /// <summary>True when the CORS/Origin check is effectively off (warn flag, Quickstart or <c>TrustedOrigins: "*"</c>).</summary>
    public bool IsWildcardCors => IsAllCorsAllowed || GraphQL.TrustedOrigins.Contains("*");
    public bool IsAllCorsAllowed => Insecure.warn_allow_all_cors_origins;
    public bool IsRateLimitingDisabled => Insecure.warn_disable_rate_limiting;
    public bool AreQueryLimitsRelaxed => Insecure.warn_relaxed_query_limits;
    public bool IsIntrospectionForced => Insecure.warn_enable_introspection;
    public bool IsAutoApproveEnabled => Insecure.warn_auto_approve_access_requests;
    public bool IsWebhookSignatureBypassed => Insecure.danger_bypass_webhook_signature_validation || Insecure.danger_allow_anonymous_webhooks;
    public bool AreUntrustedCertificatesAllowed => Insecure.danger_allow_untrusted_certificates || Insecure.danger_allow_insecure_transport;
    public bool IsWebhookTimestampToleranceIgnored => Insecure.warn_ignore_webhook_timestamp_tolerance;
    public bool IsWebhookTenantFallbackAllowed => Insecure.warn_fallback_default_tenant_for_webhooks;
    public bool AreExternalSystemsMockedIfUnreachable => Insecure.warn_mock_external_systems_if_unreachable;
    public bool IsMcpAuthBypassed => Insecure.danger_bypass_mcp_auth;
    public bool IsMcpUnmaskedAllowed => Insecure.warn_allow_unmasked_ai_access;
    public bool IsLakehouseAuthBypassed => Insecure.danger_bypass_lakehouse_auth;
    public bool AreUnsignedS3RequestsAllowed => Insecure.warn_allow_unsigned_s3_requests;
    public bool IsWebSqlDmlAllowed => WebSql.AllowDml || Insecure.warn_allow_websql_dml;

    /// <summary>
    /// Legacy alias (Insecure.warn_allow_websql_dml) for <see cref="WebSqlOptions.AllowDml"/>.
    /// They unlock the same DML path and are reported as WARN with the hint to use WebSql.AllowDml instead.
    /// </summary>
    public bool IsLegacyWebSqlDmlSwitchActive => Insecure.warn_allow_websql_dml;
    public bool IsWebSqlGovernanceBypassed => Insecure.danger_bypass_websql_governance;

    /// <summary>
    /// SEC H-02: OpenSchema (global or Catalog) opens catalog/OpenAPI documentation routes to anonymous callers
    /// and is therefore treated as a DANGER bypass (blocked outside Development).
    /// </summary>
    public bool IsOpenSchemaExplicitlyEnabled => OpenSchema || Catalog.OpenSchema;

    // SEC H-06 / M-34 / C-04: Legacy and container opt-ins weaken protections and are reported as WARN.
    public bool IsLegacyGlobalItsmWebhookSecretAllowed => Itsm.LegacyGlobalWebhookSecret;
    public bool IsLegacyCatalogPayloadOnlySignatureAllowed => Catalog.AllowLegacyPayloadOnlySignature;

    // SEC H-19: Consents created automatically from OpenMetadata policies bypass the approval workflow (reported as WARN).
    public bool IsOpenMetadataAutoCreateConsentsEnabled => OpenMetadata.AutoCreateConsents;

    // SEC E-01: an active egress allowlist exempts internal targets from the private-address SSRF check (reported as WARN).
    public bool IsEgressAllowlistActive =>
        Egress.TrustedInternalHosts.Any(h => !string.IsNullOrWhiteSpace(h)) ||
        Egress.TrustedInternalNetworks.Any(n => !string.IsNullOrWhiteSpace(n));

    /// <summary>
    /// True when any DANGER or WARN entry is active (see <see cref="GetAllActiveBypasses"/>). Used for developer-facing
    /// transparency only (X-Gateway-Insecure-Mode header in Development, startup banner); it does NOT decide whether the
    /// gateway may start or is healthy. Use <see cref="HasAnyDangerBypassActive"/> for enforcement decisions.
    /// </summary>
    public bool HasAnySecurityBypassActive => GetAllActiveBypasses().Count > 0;

    /// <summary>
    /// True when at least one DANGER entry is active. DANGER aborts startup outside Development and marks the
    /// "SecurityConfiguration" health component unhealthy.
    /// </summary>
    public bool HasAnyDangerBypassActive => GetActiveDangerBypasses().Count > 0;

    /// <summary>
    /// True when at least one WARN entry is active. WARN is permitted in Production but reported (startup warning,
    /// health description "degraded: ...").
    /// </summary>
    public bool HasAnyWarningActive => GetActiveWarnings().Count > 0;

    /// <summary>
    /// RR-L3-05: Explicit opt-in required to allow insecure WARN flags (such as warn_enable_introspection)
    /// in Production environments without startup termination.
    /// </summary>
    public bool AllowInsecureWarnFlagsInProduction { get; init; } = false;

    /// <summary>Active DANGER entries (prefix "DANGER:").</summary>
    public IReadOnlyList<string> GetActiveDangerBypasses() => FilterByPrefix(DangerPrefix);

    /// <summary>Active WARN entries (prefix "WARN:").</summary>
    public IReadOnlyList<string> GetActiveWarnings() => FilterByPrefix(WarnPrefix);

    public const string DangerPrefix = "DANGER:";
    public const string WarnPrefix = "WARN:";

    private List<string> FilterByPrefix(string prefix)
    {
        var result = new List<string>();
        foreach (var entry in GetAllActiveBypasses())
        {
            if (entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    // Security switch semantics (decided 2026-10-02):
    // - DANGER: genuinely not recommended. Outside Development startup is aborted (ValidateGatewayOptions) and the
    //   health component "SecurityConfiguration" reports unhealthy.
    // - WARN:   mildly security-relevant, permitted in Production but loud: startup warning, the health component stays
    //   healthy with the description "degraded: ...", and the list is shown in the Development health details.
    // - Regular options (e.g. WebSql.AllowDml) are not reported at all.
    // Configuration property names are kept for compatibility (a "warn_*" property may be classified as DANGER);
    // only the list prefix determines the classification.
    public IReadOnlyList<string> GetAllActiveBypasses()
    {
        var list = new List<string>();

        // --- DANGER ---
        if (IsAnonymousAccessAllowed) list.Add("DANGER:danger_allow_anonymous_access");
        if (IsConsentBypassed) list.Add("DANGER:danger_bypass_consent_checks");
        if (IsColumnMaskingDisabled) list.Add("DANGER:danger_disable_column_masking");
        if (IsInsecureTransportAllowed) list.Add("DANGER:danger_allow_insecure_transport");
        if (IsWebhookSignatureBypassed) list.Add("DANGER:danger_bypass_webhook_signature_validation");
        if (AreUntrustedCertificatesAllowed) list.Add("DANGER:danger_allow_untrusted_certificates");
        if (IsMcpAuthBypassed) list.Add("DANGER:danger_bypass_mcp_auth");
        if (IsLakehouseAuthBypassed) list.Add("DANGER:danger_bypass_lakehouse_auth");
        if (IsWebSqlGovernanceBypassed) list.Add("DANGER:danger_bypass_websql_governance");
        if (IsOpenSchemaExplicitlyEnabled) list.Add("DANGER:open_schema (OpenSchema / Catalog.OpenSchema)");
        if (IsMcpUnmaskedAllowed) list.Add("DANGER:warn_allow_unmasked_ai_access");
        if (AreExternalSystemsMockedIfUnreachable) list.Add("DANGER:warn_mock_external_systems_if_unreachable");
        if (IsAutoApproveEnabled) list.Add("DANGER:warn_auto_approve_access_requests");
        if (IsRateLimitingDisabled) list.Add("DANGER:warn_disable_rate_limiting");
        if (AreUnsignedS3RequestsAllowed) list.Add("DANGER:warn_allow_unsigned_s3_requests");
        if (IsWebhookTimestampToleranceIgnored) list.Add("DANGER:warn_ignore_webhook_timestamp_tolerance");
        if (IsOpenMetadataAutoCreateConsentsEnabled) list.Add("DANGER:openmetadata_auto_create_consents (OpenMetadata.AutoCreateConsents)");
        // SQ-15: DML without an affected-rows limit (WebSql.MaxAffectedRows <= 0 means unlimited)
        if (IsWebSqlDmlAllowed && WebSql.MaxAffectedRows <= 0) list.Add(DangerPrefix + "websql_unlimited_affected_rows (WebSql.MaxAffectedRows = 0 with DML enabled)");

        // --- WARN ---
        if (IsAllCorsAllowed) list.Add("WARN:warn_allow_all_cors_origins");
        if (AreQueryLimitsRelaxed) list.Add("WARN:warn_relaxed_query_limits");
        if (IsIntrospectionForced) list.Add("WARN:warn_enable_introspection");
        if (IsWebhookTenantFallbackAllowed) list.Add("WARN:warn_fallback_default_tenant_for_webhooks");
        if (IsLegacyCatalogPayloadOnlySignatureAllowed) list.Add("WARN:catalog_legacy_payload_only_signature (Catalog.AllowLegacyPayloadOnlySignature)");
        if (IsLegacyGlobalItsmWebhookSecretAllowed) list.Add("WARN:itsm_legacy_global_webhook_secret (Itsm.LegacyGlobalWebhookSecret)");
        if (AllowDevelopmentInContainer) list.Add("WARN:allow_development_in_container (AllowDevelopmentInContainer)");
        if (IsLegacyWebSqlDmlSwitchActive) list.Add("WARN:warn_allow_websql_dml (legacy alias, use WebSql.AllowDml)");
        if (IsEgressAllowlistActive) list.Add("WARN:egress_trusted_internal_allowlist (Egress.TrustedInternalHosts / Egress.TrustedInternalNetworks)");
        return list;
    }
}

/// <summary>
/// Entwickler- und Schnelleinstiegs-Optionen ("Getting Started").
/// Ermöglicht das bewusste Lockern oder Umgehen einzelner Sicherheitsbarrieren.
/// Alle Optionen tragen das Präfix 'warn_' (mittlerer Impact) oder 'danger_' (kritischer Impact).
/// </summary>
public sealed class InsecureGettingStartedOptions
{
    // --- DANGER: Kritischer Security-Impact (Hebelt Kern-Sicherheitsmechanismen komplett aus) ---

    /// <summary>
    /// [DANGER] Erlaubt vollständig anonymen Zugriff ohne Token/Authentifizierung.
    /// Ordnet anonymen Anfragen automatisch einen virtuellen Developer-Admin-Sicherheitskontext zu.
    /// </summary>
    public bool danger_allow_anonymous_access { get; init; } = false;

    /// <summary>
    /// [DANGER] Deaktiviert Zero-Trust-Consent-Prüfungen. Alle Tabellen im Metadaten-Katalog sind
    /// ohne vorherigen Genehmigungsworkflow für alle Clients sofort abfragbar.
    /// </summary>
    public bool danger_bypass_consent_checks { get; init; } = false;

    /// <summary>
    /// [DANGER] Deaktiviert sämtliche Spaltenmaskierungs- und Redaktionsregeln (Hashing, Masking, PII-Schutz).
    /// Alle Spalten werden im Klartext ausgeliefert.
    /// </summary>
    public bool danger_disable_column_masking { get; init; } = false;

    /// <summary>
    /// [DANGER] Erlaubt unverschlüsselte HTTP-Transporte und Entwickler-Secrets auch in Staging/Produktionsumgebungen.
    /// </summary>
    public bool danger_allow_insecure_transport { get; init; } = false;

    /// <summary>
    /// [DANGER] Umgeht die HMAC-SHA256-Signaturprüfung für eingehende Webhooks (ITSM, OpenMetadata etc.).
    /// Webhooks ohne Signatur oder mit ungültiger Signatur werden akzeptiert.
    /// </summary>
    public bool danger_bypass_webhook_signature_validation { get; init; } = false;

    /// <summary>
    /// [DANGER] Akzeptiert selbstsignierte, ungültige oder nicht vertrauenswürdige SSL/TLS-Zertifikate
    /// bei ausgehenden Verbindungen zu Fremdsystemen (ServiceNow, Jira, OpenMetadata, APIs).
    /// </summary>
    public bool danger_allow_untrusted_certificates { get; init; } = false;

    /// <summary>
    /// [DANGER] Erlaubt vollständig anonyme Webhook-Aufrufe ohne Authentifizierungs- oder Signatur-Header.
    /// </summary>
    public bool danger_allow_anonymous_webhooks { get; init; } = false;

    /// <summary>
    /// [DANGER] Umgeht die Authentifizierung und Session-Prüfung für den Model Context Protocol (MCP) Server.
    /// KI-Agenten können ohne API-Key/Bearer-Token auf exponierte Tools zugreifen.
    /// </summary>
    public bool danger_bypass_mcp_auth { get; init; } = false;

    /// <summary>
    /// [DANGER] Umgeht Authentifizierung und Rollenprüfungen für Apache Iceberg / Lakehouse Tabellenabfragen.
    /// </summary>
    public bool danger_bypass_lakehouse_auth { get; init; } = false;


    // --- WARN: Mittlerer / Operativer Security-Impact (Lockert Limits und Schutzschilder) ---

    /// <summary>
    /// [WARN] Lockert CORS und CSRF-Schutz: Erlaubt alle Origins ('*') und überspringt die strikte
    /// Origin/Referer-Validierung bei Browseranfragen.
    /// </summary>
    public bool warn_allow_all_cors_origins { get; init; } = false;

    /// <summary>
    /// [DANGER, Name historisch warn_] Deaktiviert IP- und SID-basiertes Rate-Limiting vollständig (keine HTTP 429 Antworten).
    /// </summary>
    public bool warn_disable_rate_limiting { get; init; } = false;

    /// <summary>
    /// [WARN] Hebt GraphQL Query-Depth- und Query-Complexity-Limits für tief verschachtelte Abfragen auf.
    /// </summary>
    public bool warn_relaxed_query_limits { get; init; } = false;

    /// <summary>
    /// [WARN] Aktiviert GraphQL-Schema-Introspektion und Banana Cake Pop Tooling in jeder Umgebung.
    /// </summary>
    public bool warn_enable_introspection { get; init; } = false;

    /// <summary>
    /// [DANGER, Name historisch warn_] Schaltet automatische Sofort-Genehmigung für Tabellenzugriffsanträge ein.
    /// </summary>
    public bool warn_auto_approve_access_requests { get; init; } = false;

    /// <summary>
    /// [DANGER, Name historisch warn_] Ignoriert die 5-Minuten-Gültigkeitsprüfung für Webhook-Timestamps (Replay-Schutz).
    /// </summary>
    public bool warn_ignore_webhook_timestamp_tolerance { get; init; } = false;

    /// <summary>
    /// [WARN] Verhindert Cross-Tenant-Abbrüche bei Webhooks durch Fallback auf den Mandanten des Antrags.
    /// </summary>
    public bool warn_fallback_default_tenant_for_webhooks { get; init; } = false;

    /// <summary>
    /// [DANGER, Name historisch warn_] Simuliert erfolgreiche Mock-Antworten, wenn externe Fremdsysteme (ServiceNow, Jira) nicht erreichbar sind.
    /// </summary>
    public bool warn_mock_external_systems_if_unreachable { get; init; } = false;

    /// <summary>
    /// [DANGER, Name historisch warn_] Deaktiviert das automatische PII- und DSGVO-Art.-9-Masking im AI Data Guardrail des MCP-Servers.
    /// Rohdaten werden unmaskiert an das Kontextfenster von KI-Agenten und LLMs gestreamt.
    /// </summary>
    public bool warn_allow_unmasked_ai_access { get; init; } = false;

    /// <summary>
    /// [DANGER, Name historisch warn_] Erlaubt unsignierte, anonyme S3/Object-Store-Anfragen an lokale MinIO- oder Test-Instanzen.
    /// </summary>
    public bool warn_allow_unsigned_s3_requests { get; init; } = false;

    /// <summary>
    /// [DANGER] Deaktiviert sämtliche RLS-, Maskierungs- und Consent-Prüfungen im WebSQL-Endpunkt (/api/v1/sql).
    /// </summary>
    public bool danger_bypass_websql_governance { get; init; } = false;

    /// <summary>
    /// [WARN] Legacy-Alias für WebSql.AllowDml: Erlaubt DML-Operationen (INSERT, UPDATE, DELETE) im WebSQL-Endpunkt (/api/v1/sql).
    /// </summary>
    public bool warn_allow_websql_dml { get; init; } = false;
}

/// <summary>
/// SEC M-01: Kestrel server limits (global defaults; individual endpoints may raise the body limit explicitly).
/// </summary>
public sealed class HostingLimitsOptions
{
    [Range(1024, 1073741824)] public long MaxRequestBodySizeBytes { get; init; } = 2 * 1024 * 1024;
    [Range(1, 1000000)] public long MaxConcurrentUpgradedConnections { get; init; } = 1000;
    /// <summary>0 = unlimited (Kestrel default).</summary>
    [Range(0, 10000000)] public long MaxConcurrentConnections { get; init; } = 0;
}

public sealed class PluginsOptions
{
    public string Directory { get; init; } = "plugins";
    public bool EnableHotReload { get; init; } = false;
    public bool RequireIntegrityManifest { get; init; } = false;
    public Dictionary<string, string> TrustedPluginHashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ReverseProxyOptions
{
    public bool Enabled { get; init; } = true;
    public List<string> KnownNetworks { get; init; } = ["127.0.0.1/32", "::1/128"];
    public List<string> KnownProxies { get; init; } = [];
}

public sealed class HighAvailabilityOptions
{
    public bool MultiNodeClusterMode { get; init; } = false;
    public int Replicas { get; init; } = 1;
    [Range(1, 30)] public int DrainDelaySeconds { get; init; } = 5;
    [Range(5, 120)] public int QueryTimeoutSeconds { get; init; } = 30;
    [Range(10, 180)] public int ShutdownTimeoutSeconds { get; init; } = 40;
    [Range(20, 300)] public int TerminationGracePeriodSeconds { get; init; } = 60;
}

public sealed class AuthenticationOptions
{
    [Required] public string Domain { get; init; } = "CORP.LOCAL";
    [Required] public string ServicePrincipalName { get; init; } = "HTTP/gql-gateway.corp.local";
    public bool RequireKerberosOnly { get; init; } = true;
    [Range(1, 60)] public int GroupCacheTtlMinutes { get; init; } = 5;
    public bool EnableTestAuthHandler { get; init; }

    public BasicAuthOptions BasicAuth { get; init; } = new();
    public EntraIdAuthOptions EntraId { get; init; } = new();
    public AdfsAuthOptions Adfs { get; init; } = new();
    public ForwardAuthOptions ForwardAuth { get; init; } = new();
}

public sealed class ForwardAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string UserHeader { get; init; } = "X-Forwarded-User";
    public string EmailHeader { get; init; } = "X-Forwarded-Email";
    public string GroupsHeader { get; init; } = "X-Forwarded-Groups";
    public string RolesHeader { get; init; } = "X-Forwarded-Roles";
    public string TenantHeader { get; init; } = "X-Forwarded-Tenant";
    public string? DefaultTenantId { get; init; } = TenantId.LegacySingleTenant.Value;
    public string? SharedSecretKeyVaultRef { get; init; }
    public string? SharedSecret { get; init; }
    public string SharedSecretHeader { get; init; } = "X-Forwarded-Secret";
    public bool RequireTrustedProxy { get; init; } = true;
    public List<string> TrustedProxies { get; init; } = [];
    public List<string> TrustedNetworks { get; init; } = [];

    /// <summary>
    /// RR-L2-02: Allowlist of roles accepted from the (proxy-asserted) roles header. Matching uses the role name
    /// after an optional "tenant:" prefix. Administrative roles (ClusterAdmin, PlatformAdmin, GatewayAdmin,
    /// GovernanceAdmin, SecurityAdmin, PrivacyAdmin, BreakGlassOperator) are never accepted from headers.
    /// </summary>
    public List<string> AllowedRoles { get; init; } = ["GatewayUser", "Consumer", "Analyst", "Developer"];

    /// <summary>
    /// RR-L2-02: When false (default), every group from the groups header is namespaced as
    /// <c>S-1-5-21-FORWARD-GRP-&lt;name&gt;</c>, including values that look like Windows SIDs, so a header can never
    /// impersonate a real AD group SID used in consents. Enable only if the upstream IdP asserts verified SIDs.
    /// </summary>
    public bool TrustUpstreamGroupSids { get; init; } = false;

    /// <summary>
    /// SEC H-1: When true, the proxy-asserted tenant header (X-Forwarded-Tenant) is honored, provided it is in AllowedTenantIds.
    /// When false (default), the tenant header is ignored and DefaultTenantId is used.
    /// </summary>
    public bool TrustUpstreamTenant { get; init; } = false;

    /// <summary>SEC H-1: Allowlist of tenant IDs accepted from the tenant header when TrustUpstreamTenant is true. Must not be empty when TrustUpstreamTenant is true.</summary>
    public List<string> AllowedTenantIds { get; init; } = [];
}

public sealed class BasicAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string Realm { get; init; } = "Autheris";
    public List<BasicAuthUserConfig> Users { get; init; } = [];

    /// <summary>RR-L2-03: Minimum PBKDF2-SHA256 iterations enforced at startup outside Development (OWASP 2023: 600 000; floor 210 000).</summary>
    public int MinimumPbkdf2Iterations { get; init; } = 210_000;

    /// <summary>RR-L2-03: Failed attempts per (user, client IP) within <see cref="FailureWindowSeconds"/> before the pair is locked out.</summary>
    public int MaxFailedAttempts { get; init; } = 10;

    /// <summary>RR-L2-03: Sliding failure window and lockout duration in seconds.</summary>
    public int FailureWindowSeconds { get; init; } = 300;

    /// <summary>RR-L2-03: Lifetime of a verified-credential cache entry in seconds (0 disables the cache).</summary>
    public int SuccessCacheSeconds { get; init; } = 30;

    /// <summary>
    /// F-AUTH-DX: Optional cookie session issued after a successful Basic login (developer experience).
    /// Only permitted in the environments listed in <see cref="BasicAuthSessionOptions.AllowedEnvironments"/>,
    /// never in Production.
    /// </summary>
    public BasicAuthSessionOptions Session { get; init; } = new();
}

/// <summary>
/// F-AUTH-DX: Cookie session for Basic authentication. After one successful Basic login the gateway issues an
/// encrypted, HttpOnly, SameSite=Strict cookie so browsers, cookie jars (curl/Postman/PowerShell), WebSockets and
/// EventSource keep the identity without re-sending (and re-verifying) the password.
/// </summary>
public sealed class BasicAuthSessionOptions
{
    /// <summary>Authentication scheme (and identity authentication type) of the session cookie.</summary>
    public const string SchemeName = "BasicSession";

    public bool Enabled { get; init; } = false;

    /// <summary>Environments in which the session cookie may be enabled. "Production" is never accepted.</summary>
    public List<string> AllowedEnvironments { get; init; } = ["Development"];

    /// <summary>Cookie name; must use the <c>__Host-</c> prefix (Secure, Path=/, no Domain).</summary>
    public string CookieName { get; init; } = "__Host-Autheris.Session";

    /// <summary>Sliding idle timeout of the session in minutes.</summary>
    [Range(5, 1440)] public int SlidingExpirationMinutes { get; init; } = 480;

    /// <summary>Absolute session lifetime in minutes, independent of activity.</summary>
    [Range(5, 10080)] public int AbsoluteExpirationMinutes { get; init; } = 1440;

    /// <summary>
    /// Directory for the ASP.NET Data Protection key ring. Required with multiple replicas (shared volume);
    /// otherwise the framework default (user profile) is used so cookies survive restarts.
    /// </summary>
    public string? KeyDirectory { get; init; }
}

public sealed class BasicAuthUserConfig
{
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? Sid { get; init; }
    public string? TenantId { get; init; } = Autheris.Domain.Common.TenantId.LegacySingleTenant.Value;
    public List<string> Roles { get; init; } = [];
    public List<string> GroupSids { get; init; } = [];
}

public sealed class EntraIdAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string Instance { get; init; } = "https://login.microsoftonline.com/";
    public string TenantId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string MetadataAddress { get; init; } = string.Empty;
    public bool RequireHttpsMetadata { get; init; } = true;
    public string SidClaimType { get; init; } = "oid";
    public string GroupsClaimType { get; init; } = "groups";
    public string RolesClaimType { get; init; } = "roles";
}

public sealed class AdfsAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string Authority { get; init; } = string.Empty;
    public string MetadataAddress { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public bool RequireHttpsMetadata { get; init; } = true;
    public string SidClaimType { get; init; } = "primarysid";
    public string GroupSidClaimType { get; init; } = "groupsid";
    public string RolesClaimType { get; init; } = "role";
}

public sealed class GovernanceDbOptions
{
    public string Provider { get; init; } = "Sqlite"; // "Sqlite" or "PostgreSql" ("Postgres", "PgSql")
    public string ConnectionString { get; init; } = "Data Source=governance.db;Cache=Shared";
    [Range(1, 60)] public int CommandTimeoutSeconds { get; init; } = 15;
    public bool EnableOutboxProcessor { get; init; } = true;
    public bool? SeedDemoData { get; init; } = null;
    public string? AuditHmacKeyVaultRef { get; init; }

    /// <summary>
    /// Review PG-7: optional connection string of a separate role that applies the schema (DDL) at startup (PostgreSQL). When set,
    /// the runtime connection string only needs DML rights (INSERT/SELECT on AUDIT_LOG_ENTRIES), so the runtime account cannot
    /// alter or truncate the audit table.
    /// </summary>
    public string? MigrationConnectionString { get; init; }
}

public sealed class CachingOptions
{
    [Required] public L1MemoryCacheOptions L1MemoryCache { get; init; } = new();
    [Required] public RedisOptions Redis { get; init; } = new();
    [Required] public GarnetOptions Garnet { get; init; } = new();
    [Required] public EpochValidationOptions EpochValidation { get; init; } = new();
    [Required] public CdnOptions Cdn { get; init; } = new();
}

public sealed class GarnetOptions
{
    /// <summary>
    /// Wenn true, startet das Gateway einen eingebetteten Microsoft Garnet Cache-Server (RESP-kompatibel, Tsavorite-Engine).
    /// </summary>
    public bool EnableEmbeddedServer { get; init; } = false;

    /// <summary>
    /// Bind-Adresse für den eingebetteten Garnet-Server (Standard: 127.0.0.1).
    /// </summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>
    /// Port für den eingebetteten Garnet-Server (Standard: 3278).
    /// </summary>
    [Range(1024, 65535)] public int Port { get; init; } = 3278;

    /// <summary>
    /// Deaktiviert die interne Konsolenausgabe von Garnet, um stdout nicht mit Cache-Logs zu fluten.
    /// </summary>
    public bool DisableConsole { get; init; } = true;

    /// <summary>
    /// Speicherort für Persistenz-Checkpoints (optional).
    /// </summary>
    public string? CheckpointDir { get; init; }

    /// <summary>
    /// SEC H-01: Secret-Referenz für das Garnet-Passwort (--auth Password). Ohne Angabe wird pro Prozess ein
    /// zufälliges Passwort erzeugt, das nur der In-Process-Client kennt.
    /// </summary>
    public string? PasswordSecretRef { get; init; }

    /// <summary>
    /// SEC H-01: Optional TLS for the embedded Garnet server (--tls). Garnet is bound to loopback outside
    /// Development, so TLS is defense in depth only. Requires <see cref="TlsCertFile"/> (PFX).
    /// </summary>
    public bool EnableTls { get; init; } = false;

    /// <summary>
    /// SEC H-01: Path to the PFX certificate for Garnet TLS (--cert-file-name).
    /// </summary>
    public string? TlsCertFile { get; init; }

    /// <summary>
    /// SEC H-01: Secret reference for the PFX password (--cert-password); resolved via IKeyVaultSecretProvider
    /// or environment variable.
    /// </summary>
    public string? TlsCertPasswordSecretRef { get; init; }

    /// <summary>
    /// SEC H-01: Host name the in-process client expects in the Garnet server certificate (default: Host).
    /// </summary>
    public string? TlsSslHost { get; init; }

    /// <summary>
    /// SEC H-01: Optional SHA-256 or SHA-1 thumbprints (hex) of accepted Garnet server certificates
    /// (pinning, e.g. for self-signed certificates). Empty = regular chain validation.
    /// </summary>
    public List<string> TlsAllowedServerCertificateThumbprints { get; init; } = [];
}

public sealed class L1MemoryCacheOptions
{
    [Range(16, 4096)] public int SizeLimitMb { get; init; } = 512;
    [Range(1, 120)] public int DefaultTtlMinutes { get; init; } = 10;
    [Range(1, 600)] public int SensitiveTableTtlSeconds { get; init; } = 60;
}

public sealed class RedisOptions
{
    public bool Enabled { get; init; } = false;
    public string Configuration { get; init; } = "localhost:6379,abortConnect=false";
    public string InstanceName { get; init; } = "Autheris:";
    public string InvalidationChannel { get; init; } = "consent:invalidations";
    [Range(100, 10000)] public int ConnectTimeoutMs { get; init; } = 2000;
    [Range(100, 10000)] public int SyncTimeoutMs { get; init; } = 1000;

    /// <summary>
    /// SEC H-01: Secret-Referenz für das Redis-Passwort (außerhalb Development Pflicht, falls nicht im Verbindungsstring).
    /// </summary>
    public string? PasswordSecretRef { get; init; }
    /// <summary>
    /// SEC H-01: Secret-Referenz, aus der per HKDF der HMAC-Schlüssel für L2-Consent-Cache-Einträge abgeleitet wird
    /// (Standard: DataMasking.HmacSecretKeyVaultRef).
    /// </summary>
    public string? L2IntegrityKeyVaultRef { get; init; }

    /// <summary>
    /// SEC H-01: Use TLS for the Redis connection. Outside Development TLS is mandatory for non-loopback endpoints
    /// (start fails otherwise). <c>ssl=true</c> in the connection string has the same effect.
    /// </summary>
    public bool UseTls { get; init; } = false;

    /// <summary>
    /// SEC H-01: Expected host name in the Redis server certificate (SNI/validation), if it differs from the endpoint.
    /// </summary>
    public string? SslHost { get; init; }

    /// <summary>
    /// SEC H-01: Optional SHA-256 or SHA-1 thumbprints (hex) of accepted Redis server certificates (pinning).
    /// When set, only these certificates are accepted; empty = regular chain and host name validation.
    /// </summary>
    public List<string> AllowedServerCertificateThumbprints { get; init; } = [];
}

public sealed class EpochValidationOptions
{
    public bool FailClosedOnSensitiveTables { get; init; } = true;
    [Range(1, 300)] public int DegradedMaxStalenessSeconds { get; init; } = 30;
    public bool PipelinedMGetEnabled { get; init; } = true;
}

public sealed class RateLimitingOptions
{
    [Required] public PreAuthIpRateLimitOptions PreAuthIpRateLimit { get; init; } = new();
    [Required] public PostAuthSidRateLimitOptions PostAuthSidRateLimit { get; init; } = new();
}

public sealed class PreAuthIpRateLimitOptions
{
    [Range(1, 100000)] public int PermitLimit { get; init; } = 100;
    [Range(1, 3600)] public int WindowSeconds { get; init; } = 60;
    public int QueueLimit { get; init; }
}

public sealed class PostAuthSidRateLimitOptions
{
    [Range(10, 100000)] public int TokenBucketCapacity { get; init; } = 500;
    [Range(1, 10000)] public int TokensPerSecond { get; init; } = 50;
    [Range(100, 1000000)] public int MaxCostPerMinute { get; init; } = 10000;
}

public sealed class GraphQLOptions
{
    public string EndpointPath { get; init; } = "/graphql";
    [Range(1, 25)] public int MaxAllowedExecutionDepth { get; init; } = 6;
    [Range(100, 10000)] public int MaxAllowedComplexity { get; init; } = 500;
    public bool EnableIntrospection { get; init; }
    public bool PersistedQueriesOnly { get; init; }

    /// <summary>
    /// SEC H-08: Directory containing the trusted GraphQL documents (*.graphql / *.gql) that are allowed
    /// when PersistedQueriesOnly is true. Required when PersistedQueriesOnly is enabled (fail-fast at startup).
    /// </summary>
    public string TrustedDocumentsDirectory { get; init; } = string.Empty;
    public bool EnableBananaCakePop { get; init; }
    public string BananaCakePopPath { get; init; } = "/ui/bcp";
    [Range(100, 100000)] public int MaxResponseRows { get; init; } = 5000;
    [Range(1048576, 104857600)] public long MaxResponseBytes { get; init; } = 10485760;
    [Range(10, 10000)] public int MaxInClauseBatchSize { get; init; } = 500;
    // SEC M-13: Harte Obergrenze für Root-Felder/Aliase pro GraphQL-Operation (Alias-Amplifikation).
    [Range(1, 200)] public int MaxRootFieldsPerOperation { get; init; } = 10;
    /// <summary>
    /// SEC M-14 (GAP-B): Interval in seconds after which open WebSocket subscriptions re-check whether the
    /// caller's token was revoked (ITokenRevocationService). Revoked sessions are closed.
    /// </summary>
    [Range(5, 3600)] public int SubscriptionRevalidationSeconds { get; init; } = 60;
    public List<string> TrustedOrigins { get; init; } = [];
}

public sealed class DataMaskingOptions
{
    public string HmacKeyId { get; init; } = "key-2026-q1";
    public string HmacSecretKeyVaultRef { get; init; } = "DEV_INSECURE_TEST_KEY_ONLY";
    [Range(1, 168)] public int MaskingCacheTtlHours { get; init; } = 24;

    /// <summary>
    /// RR-L6-02: When true, prevents binding derived HMAC keys as command parameters in WebSQL in-DB expressions
    /// to avoid key exposure in DB server logs (log_statement), plan cache, profilers or SIEM, failing closed to redaction.
    /// </summary>
    public bool PreventInDbHmacKeyExposure { get; init; } = false;
}

public sealed class WormAuditOptions
{
    public bool Enabled { get; init; } = false;
    public string StorageType { get; init; } = "Local"; // "Local" | "S3"
    public string ExportPath { get; init; } = string.Empty;
    public string S3Endpoint { get; init; } = string.Empty;
    public string S3Bucket { get; init; } = string.Empty;
    public string S3Prefix { get; init; } = "audit-worm-archives/";
    public string S3AccessKey { get; init; } = string.Empty;
    public string S3SecretKey { get; init; } = string.Empty;
    [Range(1, 7300)] public int RetentionDays { get; init; } = 3650;
    public string ObjectLockMode { get; init; } = "COMPLIANCE"; // "COMPLIANCE" | "GOVERNANCE"
    public bool EnforceObjectLock { get; init; } = true;
}

public sealed class AuditOptions
{
    public bool TierAEnabled { get; init; } = true;
    [Range(1, 3600)] public int TierBAggregationWindowSeconds { get; init; } = 60;
    [Range(1, 7300)] public int AuditLogRetentionDays { get; init; } = 3650;
    [Range(1, 168)] public int VerifyHashChainIntervalHours { get; init; } = 24;

    /// <summary>Review E-11: verify the audit hash chain periodically at runtime (first run shortly after start).</summary>
    public bool VerifyHashChainEnabled { get; init; } = true;

    /// <summary>Review E-11: a broken audit hash chain turns the readiness probe unhealthy (set false to only log it).</summary>
    public bool FailReadinessOnChainViolation { get; init; } = true;
    public string ElasticsearchSinkUrl { get; init; } = string.Empty;
    public WormAuditOptions Worm { get; init; } = new();

    /// <summary>
    /// SEC H-17: Pfad der extern (außerhalb der Governance-DB) gehaltenen, HMAC-signierten Endanker-Datei der
    /// Audit-Hash-Kette. Leer = "&lt;DB-Datei&gt;.audit-anchor.json" (bei In-Memory-DB: nur im Prozess).
    /// </summary>
    public string ChainAnchorPath { get; init; } = string.Empty;
}

public sealed class OpenMetadataOptions
{
    public bool Enabled { get; init; }
    public string ServerUrl { get; init; } = "http://localhost:8585/api/v1";
    public string AuthToken { get; init; } = string.Empty;
    public string WebhookSecret { get; init; } = string.Empty;
    public string ServiceFilter { get; init; } = string.Empty;
    [Range(1, 1440)] public int SyncIntervalMinutes { get; init; } = 30;
    public Dictionary<string, string> TagToMaskingRuleMap { get; init; } = new()
    {
        ["PII.Sensitive"] = "REDACT",
        ["PII.Email"] = "MASK_EMAIL",
        ["PII.Pseudonym"] = "HMAC_SHA256",
        ["PersonalData.Personal"] = "REDACT"
    };
    public Dictionary<string, string> TeamToGroupSidMap { get; init; } = new();
    public Dictionary<string, string> UserToUserSidMap { get; init; } = new();

    /// <summary>
    /// EX-01: Explicit allowlist/mapping of OpenMetadata role names to Gateway roles.
    /// Only roles present in this map are converted into gateway role consents.
    /// </summary>
    public Dictionary<string, string> RoleToGatewayRoleMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SEC H-19: Wenn true, legt der OpenMetadata-Sync Allow-Consents aus OM-Policies (nur ViewAll/ViewSampleData,
    /// ohne Condition) automatisch an. Standard: false – Vorschläge werden nur protokolliert.
    /// </summary>
    public bool AutoCreateConsents { get; init; } = false;

    /// <summary>
    /// SEC E-09 / EX-06: Explizite Zuordnung "service.database" (OpenMetadata) → Gateway-Domain. Ist die Map gesetzt,
    /// werden Tabellen ohne Eintrag abgelehnt. Leer: Domain = Service-Name (bisheriges Verhalten); Tabellen mit gleicher
    /// service.schema.table aus verschiedenen Datenbanken werden dann abgelehnt statt sich gegenseitig zu überschreiben.
    /// </summary>
    public Dictionary<string, string> ServiceDatabaseToDomainMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SEC E-09 / EX-06: Neu aus OpenMetadata (Sync, Webhook, Katalog-Provider OpenMetadata) angelegte Tabellen sind
    /// aktiv. Standard: false – neue Tabellen werden inaktiv angelegt, bis ein Gateway-Admin sie freigibt.
    /// </summary>
    public bool ActivateNewTables { get; init; } = false;

    /// <summary>
    /// EX-03: Default tenant assigned to OpenMetadata imported consents and catalog entries.
    /// Standard: "legacy-single-tenant". Kann auf einen konkreten Mandanten gesetzt werden.
    /// </summary>
    public string DefaultTenantId { get; init; } = TenantId.LegacySingleTenant.Value;

    /// <summary>
    /// EX-03: Explizite Zuordnung "service.database" oder "service" (OpenMetadata) → Gateway-Tenant.
    /// Ist die Map gesetzt, werden Tabellen primär über diese Zuordnung dem Mandanten zugewiesen.
    /// </summary>
    public Dictionary<string, string> ServiceDatabaseToTenantMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SqlDataSourceOptions
{
    public Dictionary<string, DataSourceConnectionOptions> Connections { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DataSourceConnectionOptions
{
    public string Provider { get; init; } = "Sqlite"; // "Sqlite", "SqlServer", "PostgreSql"
    public string ConnectionString { get; init; } = string.Empty;
    [Range(1, 300)] public int CommandTimeoutSeconds { get; init; } = 30;
}

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
    /// SEC E-08: Explizite Zuordnung Alation-Datenquelle (<c>ds_id</c>, numerisch) → Gateway-Domain.
    /// Tabellen nicht gemappter Datenquellen werden verworfen (keine Geister-Tabellen).
    /// </summary>
    public Dictionary<string, string> DataSourceToDomainMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SEC E-08: Nur in Development: <see cref="ApiToken"/> darf ein Klartext-Token sein, wenn er nicht über den
    /// Secret-Provider aufgelöst werden kann. Standard: false (fail-closed in allen Umgebungen).
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
    /// Open Schema Mode: Wenn true, dürfen alle Benutzer (auch ohne GovernanceAdmin/CatalogReader Rollen)
    /// den gesamten Datenkatalog, OpenAPI-Spezifikationen, Indexe und Tabellenschemata einsehen.
    /// Standard: false (Disabled - Zero-Trust Role-Enforcement aktiv).
    /// </summary>
    public bool OpenSchema { get; init; } = false;

    /// <summary>
    /// Wenn true, können alle authentifizierten Benutzer den vollständigen Metadaten-Katalog
    /// einsehen (für Data Discovery und Zugriffsbeantragung). Der Datenzugriff selbst bleibt strikt durch Consents geschützt.
    /// Standard: false (Zero-Trust: Benutzer sehen im Katalog nur Tabellen, für die sie Consents besitzen).
    /// </summary>
    public bool AllowAuthenticatedCatalogDiscovery { get; init; } = false;

    /// <summary>
    /// SEC M-34: Erlaubt (abwärtskompatibel) Katalog-Webhook-Signaturen nur über den Payload statt über
    /// "{unixTimestamp}.{payload}". Standard: false.
    /// </summary>
    public bool AllowLegacyPayloadOnlySignature { get; init; } = false;

    public PurviewOptions Purview { get; init; } = new();
    public CollibraOptions Collibra { get; init; } = new();
    public AlationOptions Alation { get; init; } = new();

    public string OpenLineageEndpoint { get; init; } = "http://localhost:5000/api/v1/lineage";
    public string OpenLineageApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Rangfolge der Dokumentationsquellen von höchster zu niedrigster Priorität.
    /// Quellen mit niedrigerem Rang können bestehende Beschreibungen ranghöherer Quellen nicht überschreiben.
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

public sealed class McpOptions
{
    public bool Enabled { get; init; } = false;
    public string EndpointPath { get; init; } = "/mcp";
    [Range(256, 128000)] public int MaxTokensPerCall { get; init; } = 4096;
    [Range(1, 10000)] public int MaxResultRows { get; init; } = 100;
    public bool RequirePiiMasking { get; init; } = true;
    public List<string> AllowedOperations { get; init; } = [];

}

public sealed class LakehouseStorageOptions
{
    public string Provider { get; init; } = "Local"; // "Local" | "S3" | "AzureBlob"
    public string LocalBasePath { get; init; } = string.Empty;
    public string S3Endpoint { get; init; } = string.Empty;
    public string S3Bucket { get; init; } = string.Empty;
    public string S3AccessKey { get; init; } = string.Empty;
    public string S3SecretKey { get; init; } = string.Empty;
    public string AzureAccountName { get; init; } = string.Empty;
    public string AzureContainer { get; init; } = string.Empty;
    public string AzureAccountKey { get; init; } = string.Empty;

    /// <summary>SEC: Maximale Byte-Anzahl beim Lesen von Metadaten-/Manifest-Dateien (Standard 64 MB).</summary>
    public long MaxReadBytes { get; init; } = 64L * 1024 * 1024;
}

public sealed class LakehouseTableOptions
{
    public string Format { get; init; } = "Iceberg";
    public string Location { get; init; } = string.Empty;
    public List<string> PartitionColumns { get; init; } = [];
    public string Sensitivity { get; init; } = "LOW";
}

public sealed class LakehouseOptions
{
    public bool Enabled { get; init; } = false;
    [Range(1, 1440)] public int MetadataCacheTtlMinutes { get; init; } = 15;
    [Range(1, 64)] public int MaxConcurrentFileScans { get; init; } = 16;
    [Range(1, 500000)] public int MaxScanRowsLimit { get; init; } = 50000;
    public LakehouseStorageOptions Storage { get; init; } = new();
    public Dictionary<string, LakehouseTableOptions> Tables { get; init; } = new(StringComparer.OrdinalIgnoreCase);

}

public sealed class ExtensibilityOptions
{
    public bool Enabled { get; init; } = true;
    public bool EnableBreakGlass { get; init; } = true;
    public bool RequireJustificationForBreakGlass { get; init; } = true;
    // SEC M-06: Break-glass requires an authorized role by default.
    public bool RequireRoleForBreakGlass { get; init; } = true;
    public List<string> BreakGlassAllowedRoles { get; init; } = ["BreakGlassOperator", "ClusterAdmin", "GovernanceAdmin", "SecurityAdmin"];
    public string JustificationHeaderName { get; init; } = "X-Access-Justification";
    public string BreakGlassHeaderName { get; init; } = "X-Break-Glass";
    public string PluginDirectory { get; init; } = "plugins";
}

public sealed class CasbinOptions
{
    public bool Enabled { get; init; } = true;
    public bool EnforceInQueryPipeline { get; init; } = true;
    public string? ModelPath { get; init; }
    public string? PolicyPath { get; init; }
}

public sealed class DbtOptions
{
    public bool Enabled { get; init; } = true;
    public string WebhookSecret { get; init; } = string.Empty;
}

public sealed class BackstageIntegrationOptions
{
    public bool Enabled { get; init; } = true;
    public string DefaultOwner { get; init; } = "group:default/data-stewards";
    public string DefaultSystem { get; init; } = "enterprise-data-mesh";
    public string DefaultNamespace { get; init; } = "default";
    public bool IncludeTablesAsApis { get; init; } = true;
    public string BaseUrl { get; init; } = string.Empty;
}

public sealed class ResourceGroupsOptions
{
    public bool Enabled { get; init; } = true;
    public ResourceGroupTierConfigOptions Interactive { get; init; } = new(50, 20, 5);
    public ResourceGroupTierConfigOptions AutonomousAgents { get; init; } = new(10, 50, 15);
    public ResourceGroupTierConfigOptions BulkAnalytics { get; init; } = new(5, 100, 60);

    /// <summary>
    /// SEC H-07: Long-lived connections (WebSocket upgrades, SSE) do not occupy resource group slots;
    /// instead they are capped per principal (SID) and per tenant.
    /// </summary>
    [Range(1, 10000)] public int MaxPersistentConnectionsPerPrincipal { get; init; } = 5;
    [Range(1, 100000)] public int MaxPersistentConnectionsPerTenant { get; init; } = 50;

    /// <summary>
    /// SEC H-07: Share of a tier's MaxConcurrency a single tenant may hold at once (percent, min. 1 slot).
    /// The tenant's queue share is derived proportionally from MaxQueueDepth.
    /// </summary>
    [Range(1, 100)] public int MaxConcurrentPerTenantPercent { get; init; } = 50;

    /// <summary>
    /// SEC H-07: Absolute per-tenant concurrency limit per tier (capped at the tier's MaxConcurrency).
    /// 0 = use <see cref="MaxConcurrentPerTenantPercent"/>.
    /// </summary>
    [Range(0, 1000)] public int MaxConcurrentPerTenant { get; init; } = 0;
}

public sealed record ResourceGroupTierConfigOptions(
    [Range(1, 1000)] int MaxConcurrency,
    [Range(0, 10000)] int MaxQueueDepth,
    [Range(1, 600)] int TimeoutSeconds
);

public sealed class SystemMetricsOptions
{
    public bool Enabled { get; init; } = true;
    public bool ExposeRestEndpoints { get; init; } = true;
    public List<string> AllowedRoles { get; init; } = ["GovernanceAdmin", "ClusterAdmin", "SecurityAdmin"];
}

public sealed class GoldenQueryOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxResultsPerRequest { get; init; } = 20;
    public List<GoldenQueryDefinition> InitialQueries { get; init; } = [];
}

public sealed record GoldenQueryDefinition(
    string Id,
    string Domain,
    string TableName,
    string Title,
    string Description,
    string QueryText,
    string? VariablesJson = null,
    List<string>? Tags = null
);

public sealed class ParquetEgressOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxRowsPerFile { get; init; } = 100000;
    public bool FlattenNestedStructures { get; init; } = true;

    /// <summary>Parquet column compression codec: None | Snappy | Gzip (invalid values fall back to Snappy).</summary>
    public string Compression { get; init; } = "Snappy";

    /// <summary>Maximum size of a buffered JSON source response (GraphQL) that is converted to Parquet.</summary>
    public long MaxBufferedSourceBytes { get; init; } = 64 * 1024 * 1024;
}

public sealed class HitLStepUpOptions
{
    public bool Enabled { get; init; } = true;
    public int ApprovalTimeoutSeconds { get; init; } = 15;
    public bool RequireDifferentApprover { get; init; } = true;
    public bool AutoCreateItsmTicket { get; init; } = true;
    public ItsmSystemType PreferredItsmSystem { get; init; } = ItsmSystemType.ServiceNow;
}

public sealed class SingleQueryPushdownOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxSubqueryDepth { get; init; } = 5;
    public bool FallbackToBatchingOnUnsupportedDialect { get; init; } = true;
    public List<DatabaseDialect> SupportedDialects { get; init; } =
    [
        DatabaseDialect.SqlServer,
        DatabaseDialect.PostgreSql,
        DatabaseDialect.Sqlite
    ];
}

public sealed class WebSqlOptions
{
    // SEC C-01/C-03: WebSQL is opt-in (secure default).
    public bool Enabled { get; init; } = false;
    public bool AllowDml { get; init; } = false;
    public long DefaultMaxRows { get; init; } = 1000;
    public long MaxAllowedRows { get; init; } = 10000;
    public int MaxQueryLength { get; init; } = 64_000;
    public int ExecutionTimeoutSeconds { get; init; } = 30;
    public string DefaultDataSourceName { get; init; } = "default";

    /// <summary>
    /// SEC C-03: Additional data sources (keys of DataSources.Connections) a WebSQL request may target.
    /// DefaultDataSourceName is always allowed; empty list = only DefaultDataSourceName.
    /// </summary>
    public List<string> AllowedDataSources { get; init; } = [];

    /// <summary>
    /// SEC C-03: Optional per-tenant data source allowlist (tenant id -> data sources). If the tenant has an
    /// entry, only these data sources are allowed, intersected with the global allowlist
    /// (DefaultDataSourceName + AllowedDataSources). Tenants without an entry keep the global allowlist.
    /// Applies to WebSQL and SQL endpoints (both run through GovernedSqlExecutionService).
    /// </summary>
    public Dictionary<string, List<string>> TenantDataSourceAllowlist { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SEC M-20: Roles that are authorized to execute DML via WebSQL (in addition to AllowDml).
    /// Consent and Casbin only evaluate read access, so DML is rejected unless the caller holds one of these roles.
    /// </summary>
    public List<string> DmlWriterRoles { get; init; } = [];

    /// <summary>
    /// DML guardrail: maximum number of rows a single WebSQL DML statement may affect. The statement runs in a
    /// transaction; if more rows are affected it is rolled back and rejected. 0 = unlimited.
    /// </summary>
    public long MaxAffectedRows { get; init; } = 1000;

    /// <summary>
    /// SQ-06 / SEC P-02: Additional SQL function names (unqualified, case-insensitive) permitted in WebSQL on top of the
    /// curated allowlist of the target dialect (TrinoSqlEngine.SqlFunctionAllowlists). Names on the built-in function
    /// denylist are never permitted, even if listed here.
    /// </summary>
    public List<string> AdditionalAllowedFunctions { get; init; } = [];

    /// <summary>
    /// Legacy alias for <see cref="AllowDml"/> (reported as WARN with the hint to use WebSql.AllowDml).
    /// </summary>
}

public sealed class SqlEndpointsOptions
{
    public bool Enabled { get; init; } = true;
    public string Directory { get; init; } = "queries";
    public bool EnableHotReload { get; init; } = true;

    /// <summary>
    /// Writes dbt models as SQL endpoint files. Note: SQL endpoints (also dbt-generated ones) are executed via
    /// GovernedSqlExecutionService and therefore require <c>WebSql.Enabled = true</c> (default: false); a model's
    /// <c>-- @datasource</c> (dbt database) must be <c>WebSql.DefaultDataSourceName</c> or listed in <c>WebSql.AllowedDataSources</c>.
    /// </summary>
    public bool AutoSyncFromDbt { get; init; } = true;
    public int MaxQueryTimeoutSeconds { get; init; } = 60;

    /// <summary>F-SQL-02: Governed stored procedure endpoints (disabled by default).</summary>
    public ProcedureEndpointsOptions Procedures { get; init; } = new();
}

/// <summary>
/// F-SQL-02: Options for governed stored procedure endpoints (SQL Server). The procedure connection must use a dedicated
/// technical login with EXECUTE on the allowed schemas only (ADR-018).
/// </summary>
public sealed class ProcedureEndpointsOptions
{
    public bool Enabled { get; init; } = false;

    /// <summary>Directory containing <c>*.proc.sql</c> declaration files.</summary>
    public string Directory { get; init; } = "procedures";

    /// <summary>Key in <c>DataSources.Connections</c> of the EXECUTE-only technical login.</summary>
    public string ConnectionName { get; init; } = "procedures";

    /// <summary>Additional data source names a declaration may use (the connection name is always allowed).</summary>
    public List<string> AllowedDataSources { get; init; } = [];

    /// <summary>Only procedures in these schemas can be exposed. Empty = nothing can be exposed (fail-closed).</summary>
    public List<string> AllowedSchemas { get; init; } = [];

    [Range(1, 1440)] public int RevalidationIntervalMinutes { get; init; } = 15;
    [Range(0, 60000)] public int LockTimeoutMs { get; init; } = 5000;
    [Range(1, 100000)] public int MaxRows { get; init; } = 5000;
    [Range(1, 1000000)] public int MaxStringParameterLength { get; init; } = 4000;
    [Range(1, 300)] public int MaxTimeoutSeconds { get; init; } = 60;
    public bool EnableHotReload { get; init; } = true;

    /// <summary>
    /// Review P-1: Declarations with <c>validation: declared</c> skip the catalog validation (no check of SECURITY POLICY,
    /// permissions, dynamic SQL or write access). They are accepted outside the Development environment only when this
    /// switch is set explicitly. Even then they need <c>result_table</c> and non-empty <c>outputs</c>, and the declared
    /// outputs are governed with the consent column rules of the result table.
    /// </summary>
    public bool AllowDeclaredValidation { get; init; } = false;
}

public sealed class MssqlChangeTrackingOptions
{
    public bool Enabled { get; init; } = false;
    public string ConnectionString { get; init; } = string.Empty;
    public int PollingIntervalMilliseconds { get; init; } = 1000;
    public int BatchSize { get; init; } = 500;
    public List<string> TrackedTables { get; init; } = [];
}

/// <summary>
/// F-CDC-03: Zero-Kafka PostgreSQL CDC via Logical Streaming Replication (pgoutput).
/// </summary>
public sealed class PostgreSqlCdcOptions
{
    public bool Enabled { get; init; } = false;
    public string ConnectionString { get; init; } = string.Empty;
    public string SlotName { get; init; } = "autheris_gateway_cdc_slot";
    public string PublicationName { get; init; } = "autheris_gateway_pub";
    public List<string> TrackedTables { get; init; } = [];
    public long MaxLagBytes { get; init; } = 1_000_000_000L; // 1 GB protection limit
    public int AckIntervalMilliseconds { get; init; } = 1000;
}

/// <summary>
/// F-OPS-01: AST-Aware Production Traffic Shadowing &amp; Dark Replay.
/// </summary>
public sealed class TrafficShadowingOptions
{
    public bool Enabled { get; init; } = false;
    public string TargetBaseUrl { get; init; } = "https://staging-gateway.internal:5001";
    public double SampleRatePercentage { get; init; } = 5.0; // 0.1% to 100%
    public int ChannelCapacity { get; init; } = 5000;
    public int TimeoutMs { get; init; } = 3000;
    public bool StripPiiHeaders { get; init; } = true;
}

/// <summary>
/// F-AI-08: FOCUS-compliant FinOps Accounting for Token &amp; Compute.
/// Implements standard cost attribution, usage metering, and budget governance caps.
/// </summary>
public sealed class FinOpsOptions
{
    public bool Enabled { get; init; } = false;
    public decimal DefaultMonthlyBudget { get; init; } = 1000m;
    public double SoftCapRatio { get; init; } = 0.8; // 80% warning
    public decimal PricePerThousandPromptTokens { get; init; } = 0.003m;
    public decimal PricePerThousandCompletionTokens { get; init; } = 0.015m;
    public decimal PricePerComputeSecond { get; init; } = 0.0001m;
    public Dictionary<string, decimal> TenantMonthlyBudgets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// F-GOV-08: Dynamic Schema Contracts &amp; Tag-based Projection.
/// Allows slicing the supergraph into isolated contract views (@tag / @inaccessible) for partners, mobile, and web.
/// </summary>
public sealed class SchemaContractsOptions
{
    public bool Enabled { get; init; } = false;
    public string DefaultContract { get; init; } = "default";
    public Dictionary<string, SchemaContractDefinitionOptions> Contracts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SchemaContractDefinitionOptions
{
    public List<string> IncludedTags { get; init; } = [];
    public List<string> ExcludedTags { get; init; } = [];
    public bool ExcludeInaccessible { get; init; } = true;
}

/// <summary>
/// Outbound egress allowlist for integrations (ITSM, catalogs, OpenMetadata, lineage, lakehouse) that run inside the
/// corporate network. The SSRF guard blocks private/loopback targets by default; explicitly trusted internal hosts or
/// networks are exempt from the private-address check. Cloud metadata endpoints and link-local addresses stay blocked,
/// and HTTPS remains mandatory outside Development.
/// </summary>
public sealed class OutboundEgressOptions
{
    /// <summary>Exact host names (case-insensitive, without port), e.g. "jira.corp.local".</summary>
    public List<string> TrustedInternalHosts { get; init; } = [];

    /// <summary>CIDR networks, e.g. "10.20.0.0/16". Resolved addresses inside these networks are permitted.</summary>
    public List<string> TrustedInternalNetworks { get; init; } = [];

    /// <summary>
    /// SEC E-02: integrations that may use the trusted hosts/networks above. Null (not configured) = default
    /// <c>Itsm, Catalog, OpenMetadata, Lineage</c>. Allowed names: Itsm, Catalog, OpenMetadata, Lineage, AuditWorm, Cdn.
    /// Lakehouse can never use the allowlist (its target URLs come from Iceberg manifests, i.e. producer data).
    /// Nullable on purpose: the configuration binder appends to pre-filled lists, so a non-empty default could not be narrowed.
    /// </summary>
    public List<string>? TrustedIntegrations { get; init; }

    private static readonly string[] DefaultTrustedIntegrations = ["Itsm", "Catalog", "OpenMetadata", "Lineage"];

    /// <summary>Configured <see cref="TrustedIntegrations"/> or the default list.</summary>
    public IReadOnlyList<string> GetEffectiveTrustedIntegrations() => TrustedIntegrations ?? (IReadOnlyList<string>)DefaultTrustedIntegrations;
}

/// <summary>
/// F-PERF-12: Incremental Delivery Options (@defer &amp; @stream).
/// Governs multipart streaming chunk delivery and connection resource bounds.
/// </summary>
public sealed class IncrementalDeliveryOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxDeferredExecutionTimeMs { get; init; } = 30000;
    public int MaxConcurrentStreamsPerClient { get; init; } = 10;
    public int MaxIncrementalChunks { get; init; } = 100;
}

/// <summary>
/// F-SEC-04: Relationship-Based Access Control (ReBAC) Options.
/// Configures Zanzibar/OpenFGA graph traversal depth, caching, and batch DataLoader settings.
/// </summary>
public sealed class RebacOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxTraversalDepth { get; init; } = 10;
    public int CacheTtlSeconds { get; init; } = 60;
    public int MaxCachedDecisions { get; init; } = 50000;
    public bool EnforceOnStreaming { get; init; } = false;
    public string? OpenFgaApiUrl { get; init; }
    public string? OpenFgaStoreId { get; init; }
}

/// <summary>
/// F-DATA-04: Native Apache Arrow IPC & Flight SQL Egress Options.
/// </summary>
public sealed class ArrowExportOptions
{
    public bool Enabled { get; init; } = true;
    public int BatchSize { get; init; } = 64000;
    public int MaxExportRows { get; init; } = 1000000;
    public bool EnableFlightSql { get; init; } = true;
}

/// <summary>
/// F-DATA-03: Embedded In-Memory OLAP via DuckDB.NET Options.
/// </summary>
public sealed class DuckDbOlapOptions
{
    public bool Enabled { get; init; } = true;
    public string MaxMemory { get; init; } = "1GB";
    public int MaxStagedRowsPerTable { get; init; } = 250000;
    public int QueryTimeoutSeconds { get; init; } = 60;
    public int MaxThreads { get; init; } = 2;
    public bool EnableCrossDomainJoinOptimization { get; init; } = true;
}


