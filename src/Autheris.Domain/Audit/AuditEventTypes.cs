namespace Autheris.Domain.Audit;

/// <summary>
/// Canonical catalog of all audit event types in Autheris (PLAN-AUDIT-02-LUECKENLOSES-ZUGRIFFS-AUDIT §3.3).
/// </summary>
public static class AuditEventTypes
{
    // Authentication & Authorization (Tier A / C)
    public const string AuthFailed = "AUTH_FAILED";
    public const string AuthTokenRejected = "AUTH_TOKEN_REJECTED";
    public const string AuthSucceeded = "AUTH_SUCCEEDED";
    public const string AuthBruteForceDetected = "AUTH_BRUTE_FORCE_DETECTED";
    public const string AuthzEndpointDenied = "AUTHZ_ENDPOINT_DENIED";
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";
    public const string TokenRevoked = "TOKEN_REVOKED";
    public const string TokenRevokedHit = "TOKEN_REVOKED_HIT";
    public const string TenantCollisionDenied = "TENANT_COLLISION_DENIED";
    public const string TenantCollisionResolved = "TENANT_COLLISION_RESOLVED";

    // Catalog & Metadata Reads (Tier B)
    public const string CatalogRead = "CATALOG_READ";
    public const string MetadataExport = "METADATA_EXPORT";

    // Data Access (Tier A / B)
    public const string TableQuery = "TABLE_QUERY";
    public const string WebSqlQuery = "WEBSQL_QUERY";
    public const string WebSqlQueryDenied = "WEBSQL_QUERY_DENIED";
    public const string WebSqlCrossSourceQuery = "WEBSQL_CROSS_SOURCE_QUERY";
    public const string WebSqlCrossSourceSourceRead = "WEBSQL_CROSS_SOURCE_SOURCE_READ";
    public const string WebSqlDmlExecuted = "WEBSQL_DML_EXECUTED";
    public const string WebSqlDmlRejected = "WEBSQL_DML_REJECTED";
    public const string WebSqlDmlFailed = "WEBSQL_DML_FAILED";
    public const string QueryExecutionError = "QUERY_EXECUTION_ERROR";
    public const string StreamSubscribe = "STREAM_SUBSCRIBE";
    public const string CdcWebhookDeliveryBatch = "CDC_WEBHOOK_DELIVERY_BATCH";
    public const string EgressShadowCopy = "EGRESS_SHADOW_COPY";

    // Administration & Audit Self-Governance (Tier A)
    public const string AuditRead = "AUDIT_READ";
    public const string AuditExport = "AUDIT_EXPORT";
    public const string AuditConfigChanged = "AUDIT_CONFIG_CHANGED";
    public const string AuditPipelineFault = "AUDIT_PIPELINE_FAULT";
    public const string AuditRetentionPurge = "AUDIT_RETENTION_PURGE";

    // System Lifecycle
    public const string ServiceStarted = "SERVICE_STARTED";
    public const string ServiceStopped = "SERVICE_STOPPED";

    // Regulatory & Consent
    public const string ConsentRequested = "CONSENT_REQUESTED";
    public const string ConsentApproved = "CONSENT_APPROVED";
    public const string ConsentGranted = "CONSENT_GRANTED";
    public const string ConsentDenied = "CONSENT_DENIED";
    public const string ConsentRevoked = "CONSENT_REVOKED";
    public const string ConsentApplied = "CONSENT_APPLIED";
    public const string ProfileInvalidationFailed = "PROFILE_INVALIDATION_FAILED";
    public const string BreakGlassActivated = "BREAK_GLASS_ACTIVATED";

    // Plan 9: Datasources & Async Jobs
    public const string DatasourceTested = "DATASOURCE_TESTED";
    public const string AsyncJobSubmitted = "ASYNC_JOB_SUBMITTED";
    public const string AsyncJobCompleted = "ASYNC_JOB_COMPLETED";
    public const string AsyncJobDownloaded = "ASYNC_JOB_DOWNLOADED";
    public const string AsyncJobCancelled = "ASYNC_JOB_CANCELLED";
    public const string AsyncJobStatusRead = "ASYNC_JOB_STATUS_READ";
    public const string AsyncJobAccessDenied = "ASYNC_JOB_ACCESS_DENIED";

    // Governance classification, access plans and DevPortal step-up consoles
    public const string ClassificationProposed = "CLASSIFICATION_PROPOSED";
    public const string ClassificationTransitioned = "CLASSIFICATION_TRANSITIONED";
    public const string GovernanceConfigHashRead = "GOVERNANCE_CONFIG_HASH_READ";
    public const string AccessPlanRead = "ACCESS_PLAN_READ";
    public const string TwoFactorEnrollmentStarted = "TWO_FACTOR_ENROLLMENT_STARTED";
    public const string TwoFactorEnrollmentVerified = "TWO_FACTOR_ENROLLMENT_VERIFIED";
    public const string StepUpApprovalsListed = "STEP_UP_APPROVALS_LISTED";
    public const string StepUpApprovalAttempted = "STEP_UP_APPROVAL_ATTEMPTED";

    // Universal Documentation Store & MCP Ingestion
    public const string DocIngestionBundleApplied = "DOC_INGESTION_BUNDLE_APPLIED";
    public const string DocIngestionDocumentApplied = "DOC_INGESTION_DOCUMENT_APPLIED";
    public const string DocDiscoveryRead = "DOC_DISCOVERY_READ";

    // ReBAC relationship administration
    public const string RebacTupleAdded = "REBAC_TUPLE_ADDED";
    public const string RebacTupleRemoved = "REBAC_TUPLE_REMOVED";
    public const string RebacInvalidationFailed = "REBAC_INVALIDATION_FAILED";
    public const string RebacTupleSeed = "REBAC_TUPLE_SEED";
}
