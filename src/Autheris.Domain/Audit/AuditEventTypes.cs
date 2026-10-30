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
    public const string TenancyDefaultTenantConfigured = "TENANCY_DEFAULT_TENANT_CONFIGURED";

    // Catalog & Metadata Reads (Tier B)
    public const string CatalogRead = "CATALOG_READ";
    public const string MetadataExport = "METADATA_EXPORT";
    public const string DataCatalogSync = "DATA_CATALOG_SYNC";

    // Data Access (Tier A / B)
    public const string TableQuery = "TABLE_QUERY";
    public const string WebSqlQuery = "WEBSQL_QUERY";
    public const string WebSqlQueryDenied = "WEBSQL_QUERY_DENIED";
    public const string WebSqlCrossSourceQuery = "WEBSQL_CROSS_SOURCE_QUERY";
    public const string WebSqlCrossSourceSourceRead = "WEBSQL_CROSS_SOURCE_SOURCE_READ";
    public const string WebSqlCrossSourceSubtreeRead = "WEBSQL_CROSS_SOURCE_SUBTREE_READ";
    public const string CrossSourceRelationResolved = "CROSS_SOURCE_RELATION_RESOLVED";
    public const string CrossSourceOptInChanged = "CROSS_SOURCE_OPT_IN_CHANGED";
    public const string WebSqlDmlExecuted = "WEBSQL_DML_EXECUTED";
    public const string WebSqlDmlRejected = "WEBSQL_DML_REJECTED";
    public const string WebSqlDmlFailed = "WEBSQL_DML_FAILED";
    public const string QueryExecutionError = "QUERY_EXECUTION_ERROR";
    public const string StreamSubscribe = "STREAM_SUBSCRIBE";
    public const string CdcWebhookDeliveryBatch = "CDC_WEBHOOK_DELIVERY_BATCH";
    public const string EgressShadowCopy = "EGRESS_SHADOW_COPY";

    // Governed CDC fan-out (issue #99, PLAN-CDC-99 D-B8): the batch event is Tier B; lifecycle/admin events are individually audited.
    public const string CdcDeliveryBatch = "CDC_DELIVERY_BATCH";
    public const string CdcConsumerRegistered = "CDC_CONSUMER_REGISTERED";
    public const string CdcConsumerRejected = "CDC_CONSUMER_REJECTED";
    public const string CdcConsumerDeleted = "CDC_CONSUMER_DELETED";
    public const string CdcConsumerPaused = "CDC_CONSUMER_PAUSED";
    public const string CdcConsumerResumed = "CDC_CONSUMER_RESUMED";
    public const string CdcConsumerOffsetReset = "CDC_CONSUMER_OFFSET_RESET";
    public const string CdcDeadLetterReplayed = "CDC_DEAD_LETTER_REPLAYED";
    public const string CdcStreamResync = "CDC_STREAM_RESYNC";
    public const string CdcStreamWatermarkReset = "CDC_STREAM_WATERMARK_RESET";
    public const string CdcStreamKeyRotated = "CDC_STREAM_KEY_ROTATED";
    public const string CdcPluginReceiverBound = "CDC_PLUGIN_RECEIVER_BOUND";
    public const string CdcPluginReceiverRejected = "CDC_PLUGIN_RECEIVER_REJECTED";

    // Administration & Audit Self-Governance (Tier A)
    public const string AuditRead = "AUDIT_READ";
    public const string AuditExport = "AUDIT_EXPORT";
    public const string AuditConfigChanged = "AUDIT_CONFIG_CHANGED";
    public const string AuditPipelineFault = "AUDIT_PIPELINE_FAULT";
    public const string AuditRetentionPurge = "AUDIT_RETENTION_PURGE";

    // Catalog definition changes (Tier A, issue #94 SEC-94-06): declared primary key / index replacement with before and after.
    public const string CatalogKeyDefinitionChanged = "CATALOG_KEY_DEFINITION_CHANGED";

    // Catalog visibility and tag changes (Tier A, issue #111): before/after, actor and source.
    public const string CatalogVisibilityChanged = "CATALOG_VISIBILITY_CHANGED";
    public const string CatalogTagsChanged = "CATALOG_TAGS_CHANGED";

    // Issue #111 (A-9): aggregated count of probe attempts on datasets the caller cannot manage (one entry per principal and window).
    public const string CatalogProbeSuppressed = "CATALOG_PROBE_SUPPRESSED";

    // System Lifecycle
    public const string ServiceStarted = "SERVICE_STARTED";
    public const string ServiceStopped = "SERVICE_STOPPED";

    // Regulatory & Consent
    public const string ConsentRequested = "CONSENT_REQUESTED";
    public const string ConsentApproved = "CONSENT_APPROVED";
    public const string ConsentGranted = "CONSENT_GRANTED";
    public const string ConsentDenied = "CONSENT_DENIED";
    public const string ConsentRevoked = "CONSENT_REVOKED";
    public const string ConsentTenantIntegrityRefused = "CONSENT_TENANT_INTEGRITY_REFUSED";
    public const string ConsentWithdrawn = "CONSENT_WITHDRAWN";
    public const string ConsentApplied = "CONSENT_APPLIED";
    public const string ConsentDescriptionChanged = "CONSENT_DESCRIPTION_CHANGED";
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

    // Governed Aggregation & Semantic Layer Metrics (#28, #37, #38)
    public const string AggregateQuery = "AGGREGATE_QUERY";
    public const string GovernedAggregation = AggregateQuery;
    public const string MetricQuery = "METRIC_QUERY";

    // Zero-Read Query Analysis & Metadata Synthetic Sample (#79, #80)
    public const string MetadataSyntheticSample = "METADATA_SYNTHETIC_SAMPLE";
    public const string QueryAnalysisEvaluated = "QUERY_ANALYSIS_EVALUATED";

    // MCP output inspection: a tool or resource result carried a suspected prompt injection and was withheld (MCP-09)
    public const string McpOutputInjectionSuspected = "MCP_OUTPUT_INJECTION_SUSPECTED";

    // Row filter key-set cache strategy administration (#107)
    public const string RowFilterEvaluationChanged = "ROW_FILTER_EVALUATION_CHANGED";
    public const string TableRowFilterStrategyChanged = "TABLE_ROW_FILTER_STRATEGY_CHANGED";

    // Caller attribute registry administration (#129; four-eyes steps carry hashes only, never values)
    public const string CallerAttributeDefinitionPending = "CALLER_ATTRIBUTE_DEFINITION_PENDING";
    public const string CallerAttributeDefinitionApplied = "CALLER_ATTRIBUTE_DEFINITION_APPLIED";
    public const string CallerAttributeDefinitionRejected = "CALLER_ATTRIBUTE_DEFINITION_REJECTED";
    public const string CallerAttributeDefinitionDeleted = "CALLER_ATTRIBUTE_DEFINITION_DELETED";
    public const string CallerAttributeServiceSetChanged = "CALLER_ATTRIBUTE_SERVICE_SET_CHANGED";
    public const string CallerAttributeSimulationRead = "CALLER_ATTRIBUTE_SIMULATION_READ";

    // Issue #281: app-scoped trusted GraphQL documents
    public const string TrustedDocumentsReplaced = "TRUSTED_DOCUMENTS_REPLACED";
    public const string TrustedDocumentsRetired = "TRUSTED_DOCUMENTS_RETIRED";
}

