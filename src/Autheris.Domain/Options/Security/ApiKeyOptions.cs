namespace Autheris.Domain.Options;

/// <summary>
/// Developer and quickstart options ("Getting Started").
/// Allows deliberate relaxation or bypass of individual security barriers in development.
/// All options carry the prefix 'warn_' (medium impact) or 'danger_' (critical impact).
/// </summary>
public sealed class InsecureGettingStartedOptions
{
    // --- DANGER: Critical security impact (disables core security mechanisms) ---

    /// <summary>
    /// [DANGER] Allows completely anonymous access without token/authentication.
    /// Automatically assigns a virtual developer-admin security context to anonymous requests.
    /// </summary>
    public bool danger_allow_anonymous_access { get; init; } = false;

    /// <summary>
    /// [DANGER] Disables Zero-Trust consent checks. All tables in the metadata catalog are
    /// immediately queryable by all clients without prior approval workflow.
    /// </summary>
    public bool danger_bypass_consent_checks { get; init; } = false;

    /// <summary>
    /// [DANGER] Disables all column masking and redaction rules (hashing, masking, PII protection).
    /// All columns are returned in cleartext.
    /// </summary>
    public bool danger_disable_column_masking { get; init; } = false;

    /// <summary>
    /// [DANGER] Allows unencrypted HTTP transport and developer secrets in staging/production environments.
    /// </summary>
    public bool danger_allow_insecure_transport { get; init; } = false;

    /// <summary>
    /// [DANGER] Bypasses HMAC-SHA256 signature verification for incoming webhooks (ITSM, OpenMetadata, etc.).
    /// Webhooks without a signature or with an invalid signature are accepted.
    /// </summary>
    public bool danger_bypass_webhook_signature_validation { get; init; } = false;

    /// <summary>
    /// [DANGER] Accepts self-signed, invalid, or untrusted SSL/TLS certificates
    /// on outbound connections to external systems (ServiceNow, Jira, OpenMetadata, APIs).
    /// </summary>
    public bool danger_allow_untrusted_certificates { get; init; } = false;

    /// <summary>
    /// [DANGER] Allows completely anonymous webhook calls without authentication or signature headers.
    /// </summary>
    public bool danger_allow_anonymous_webhooks { get; init; } = false;

    /// <summary>
    /// [DANGER] Bypasses authentication and session checks for the Model Context Protocol (MCP) server.
    /// AI agents can access exposed tools without an API key or Bearer token.
    /// </summary>
    public bool danger_bypass_mcp_auth { get; init; } = false;

    /// <summary>
    /// [DANGER] Bypasses authentication and role checks for Apache Iceberg / Lakehouse table queries.
    /// </summary>
    public bool danger_bypass_lakehouse_auth { get; init; } = false;

    /// <summary>
    /// [DANGER] Bypasses ReBAC (Relationship-Based Access Control) authorization checks for quickstart / PoC.
    /// </summary>
    public bool danger_bypass_rebac { get; init; } = false;

    // --- WARN: Medium / operational security impact (relaxes limits and shields) ---

    /// <summary>
    /// [WARN] Relaxes CORS and CSRF protection: allows all origins ('*') and skips strict
    /// Origin/Referer validation on browser requests.
    /// </summary>
    public bool warn_allow_all_cors_origins { get; init; } = false;

    /// <summary>
    /// [DANGER, legacy warn_ name] Completely disables IP- and SID-based rate limiting (no HTTP 429 responses).
    /// </summary>
    public bool warn_disable_rate_limiting { get; init; } = false;

    /// <summary>
    /// [WARN] Lifts GraphQL query depth and complexity limits for deeply nested queries.
    /// </summary>
    public bool warn_relaxed_query_limits { get; init; } = false;

    /// <summary>
    /// [WARN] Enables GraphQL schema introspection and Banana Cake Pop tooling in any environment.
    /// </summary>
    public bool warn_enable_introspection { get; init; } = false;

    /// <summary>
    /// [DANGER, legacy warn_ name] Enables automatic instant approval for table access requests.
    /// </summary>
    public bool warn_auto_approve_access_requests { get; init; } = false;

    /// <summary>
    /// [DANGER, legacy warn_ name] Ignores the 5-minute validity check for webhook timestamps (replay protection).
    /// </summary>
    public bool warn_ignore_webhook_timestamp_tolerance { get; init; } = false;

    /// <summary>
    /// [WARN] Prevents cross-tenant aborts on webhooks by falling back to the request's tenant.
    /// </summary>
    public bool warn_fallback_default_tenant_for_webhooks { get; init; } = false;

    /// <summary>
    /// [DANGER, legacy warn_ name] Simulates successful mock responses when external systems (ServiceNow, Jira) are unreachable.
    /// </summary>
    public bool warn_mock_external_systems_if_unreachable { get; init; } = false;

    /// <summary>
    /// [DANGER, legacy warn_ name] Disables automatic PII and GDPR Art. 9 masking in the AI Data Guardrail of the MCP server.
    /// Raw data is streamed unmasked to the context window of AI agents and LLMs.
    /// </summary>
    public bool warn_allow_unmasked_ai_access { get; init; } = false;

    /// <summary>
    /// [DANGER, legacy warn_ name] Allows unsigned, anonymous S3/Object-Store requests to local MinIO or test instances.
    /// </summary>
    public bool warn_allow_unsigned_s3_requests { get; init; } = false;

    /// <summary>
    /// [DANGER] Disables all RLS, masking, and consent checks in the WebSQL endpoint (/api/v1/sql).
    /// </summary>
    public bool danger_bypass_websql_governance { get; init; } = false;

    /// <summary>
    /// [WARN] Legacy alias for WebSql.AllowDml: allows DML operations (INSERT, UPDATE, DELETE) in the WebSQL endpoint (/api/v1/sql).
    /// </summary>
    public bool warn_allow_websql_dml { get; init; } = false;
}
