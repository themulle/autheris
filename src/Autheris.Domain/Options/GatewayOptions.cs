using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Domain.Options;

public sealed class GatewayOptions
{
    public const string SectionName = GatewaySections.Root;

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
    public TotpOptions Totp { get; init; } = new();
    [Required] public WebSqlOptions WebSql { get; init; } = new();
    [Required] public TransportRowLimitsOptions RowLimits { get; init; } = new();
    [Required] public AsyncJobOptions AsyncJobs { get; init; } = new();
    [Required] public SqlEndpointsOptions SqlEndpoints { get; init; } = new();
    [Required] public InsecureGettingStartedOptions Insecure { get; init; } = new();
    [Required] public DevOptions Dev { get; init; } = new();
    [Required] public MssqlChangeTrackingOptions MssqlChangeTracking { get; init; } = new();
    [Required] public PostgreSqlCdcOptions PostgreSqlCdc { get; init; } = new();
    [Required] public CdcStreamOptions CdcStream { get; init; } = new();
    [Required] public TrafficShadowingOptions TrafficShadowing { get; init; } = new();
    [Required] public FinOpsOptions FinOps { get; init; } = new();
    [Required] public SchemaContractsOptions SchemaContracts { get; init; } = new();
    [Required] public IncrementalDeliveryOptions IncrementalDelivery { get; init; } = new();
    [Required] public RebacOptions Rebac { get; init; } = new();
    [Required] public ArrowExportOptions Arrow { get; init; } = new();
    [Required] public DuckDbOlapOptions DuckDbOlap { get; init; } = new();
    [Required] public RowFilterOptions RowFilters { get; init; } = new();
    [Required] public CallerAttributeOptions CallerAttributes { get; init; } = new();
    [Required] public VirtualFilterOptions VirtualFilters { get; init; } = new();
    [Required] public LoggingOptions Logging { get; init; } = new();
    [Required] public ClassificationOptions Classification { get; init; } = new();
    [Required] public TenantIsolationOptions TenantIsolation { get; init; } = new();
    public TenancyOptions Tenancy { get; init; } = new();
    [Required] public ODataOptions OData { get; init; } = new();
    [Required] public WebhooksOptions Webhooks { get; init; } = new();
    [Required] public FieldSunsettingOptions FieldSunsetting { get; init; } = new();

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
    public bool IsRebacBypassed => Insecure.danger_bypass_rebac;
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
        if (IsRebacBypassed) list.Add("DANGER:danger_bypass_rebac");
        if (IsWebSqlGovernanceBypassed) list.Add("DANGER:danger_bypass_websql_governance");
        if (IsOpenSchemaExplicitlyEnabled) list.Add("DANGER:open_schema (OpenSchema / Catalog.OpenSchema)");
        if (IsMcpUnmaskedAllowed) list.Add("DANGER:warn_allow_unmasked_ai_access");
        if (AreExternalSystemsMockedIfUnreachable) list.Add("DANGER:warn_mock_external_systems_if_unreachable");
        if (IsAutoApproveEnabled) list.Add("DANGER:warn_auto_approve_access_requests");
        if (IsRateLimitingDisabled) list.Add("DANGER:warn_disable_rate_limiting");
        if (AreUnsignedS3RequestsAllowed) list.Add("DANGER:warn_allow_unsigned_s3_requests");
        if (IsWebhookTimestampToleranceIgnored) list.Add("DANGER:warn_ignore_webhook_timestamp_tolerance");
        if (IsOpenMetadataAutoCreateConsentsEnabled) list.Add("DANGER:openmetadata_auto_create_consents (OpenMetadata.AutoCreateConsents)");
        // API-1: Webhook tenant fallback and legacy global webhook secret allow cross-tenant spoofing and are prohibited outside Development
        if (IsWebhookTenantFallbackAllowed) list.Add("DANGER:warn_fallback_default_tenant_for_webhooks");
        if (IsLegacyGlobalItsmWebhookSecretAllowed) list.Add("DANGER:itsm_legacy_global_webhook_secret (Itsm.LegacyGlobalWebhookSecret)");
        // SQ-15: DML without an affected-rows limit (WebSql.MaxAffectedRows <= 0 means unlimited)
        if (IsWebSqlDmlAllowed && WebSql.MaxAffectedRows <= 0) list.Add(DangerPrefix + "websql_unlimited_affected_rows (WebSql.MaxAffectedRows = 0 with DML enabled)");

        // --- WARN ---
        if (IsAllCorsAllowed) list.Add("WARN:warn_allow_all_cors_origins");
        if (AreQueryLimitsRelaxed) list.Add("WARN:warn_relaxed_query_limits");
        if (IsIntrospectionForced) list.Add("WARN:warn_enable_introspection");
        if (IsLegacyCatalogPayloadOnlySignatureAllowed) list.Add("WARN:catalog_legacy_payload_only_signature (Catalog.AllowLegacyPayloadOnlySignature)");
        if (AllowDevelopmentInContainer) list.Add("WARN:allow_development_in_container (AllowDevelopmentInContainer)");
        if (IsLegacyWebSqlDmlSwitchActive) list.Add("WARN:warn_allow_websql_dml (legacy alias, use WebSql.AllowDml)");
        if (IsEgressAllowlistActive) list.Add("WARN:egress_trusted_internal_allowlist (Egress.TrustedInternalHosts / Egress.TrustedInternalNetworks)");
        return list;
    }
}
