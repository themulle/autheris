using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

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

public enum AuditCatalogReadMode
{
    Summarized,
    Full
}

public sealed class AuditRetentionOptions
{
    public const string SectionName = GatewaySections.AuditRetention;

    public bool Enabled { get; init; } = true;
    public int RetentionDays { get; init; } = 90;
    public int SecurityDays { get; init; } = 3650;
    public int DataAccessDays { get; init; } = 400;
    public int AggregatedDays { get; init; } = 90;
    public int OperationsDays { get; init; } = 30;
    public int BatchSize { get; init; } = 5000;
    public TimeSpan PurgeInterval { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan InterBatchDelay { get; init; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// SR-L-08: reaction of the audit pipeline once an audit hash chain violation has been detected. The violation state is latched
/// for the process lifetime; recovery is a restart after forensic review.
/// </summary>
public enum AuditChainViolationPolicy
{
    /// <summary>Data-access (Tier-B query) audit events are refused, so the data request fails closed; security and admin (Tier-A) events are still written and carry <c>chainViolation=true</c>.</summary>
    DenyDataAccess = 0,

    /// <summary>Every audit event (Tier-A and Tier-B) is refused.</summary>
    DenyAll = 1,

    /// <summary>The violation is logged and reported by the health check only; audit writes continue unchanged.</summary>
    AlertOnly = 2
}

public sealed class AuditOptions
{
    /// <summary>
    /// SR-L-08: reaction to a detected audit chain violation. <c>null</c> (default) selects <see cref="AuditChainViolationPolicy.DenyDataAccess"/>
    /// outside Development/Test and <see cref="AuditChainViolationPolicy.AlertOnly"/> in Development/Test.
    /// </summary>
    public AuditChainViolationPolicy? OnChainViolation { get; init; }

    [Range(1, 3600)] public int TierBAggregationWindowSeconds { get; init; } = 60;
    [Range(365, 7300)] public int AuditLogRetentionDays { get; init; } = 3650;
    public AuditCatalogReadMode CatalogReadMode { get; init; } = AuditCatalogReadMode.Summarized;
    [Range(1, 3600)] public int CatalogSummaryWindowSeconds { get; init; } = 60;
    public AuditRetentionOptions Retention { get; init; } = new();

    /// <summary>
    /// SEC-ADG-23 (X1.2): store the compiled SQL text (markers only, no values by construction) in the audit record of a governed
    /// statement. Default <c>false</c>: the record carries only the compiler version, the dialect, the bind count and the
    /// <c>sql.compiled_digest</c>.
    /// </summary>
    public bool StoreCompiledSql { get; init; }
    [Range(1, 168)] public int VerifyHashChainIntervalHours { get; init; } = 24;

    /// <summary>Review E-11: verify the audit hash chain periodically at runtime (first run shortly after start).</summary>
    public bool VerifyHashChainEnabled { get; init; } = true;

    /// <summary>Review E-11: a broken audit hash chain turns the readiness probe unhealthy (set false to only log it).</summary>
    public bool FailReadinessOnChainViolation { get; init; } = true;
    public string ElasticsearchSinkUrl { get; init; } = string.Empty;
    public WormAuditOptions Worm { get; init; } = new();

    /// <summary>
    /// SEC H-17: Path of the externally kept (outside the governance DB), HMAC-signed tail anchor file of the
    /// audit hash chain. Empty = "&lt;DB-file&gt;.audit-anchor.json" (in-memory DB: in-process only).
    /// </summary>
    public string ChainAnchorPath { get; init; } = string.Empty;

    /// <summary>
    /// SEC E-11: Directory on SEPARATE storage (ideally a WORM / object-lock backed mount) that receives every
    /// anchor as an immutable, never overwritten file (<c>anchor-&lt;seq&gt;-&lt;ticks&gt;.json</c>). Empty = off.
    /// </summary>
    public string ChainAnchorWormDirectory { get; init; } = string.Empty;

    /// <summary>SEC E-11: Additional mirror files that receive the same anchor as <see cref="ChainAnchorPath"/>.</summary>
    public string[] ChainAnchorMirrorPaths { get; init; } = [];

    /// <summary>
    /// SEC E-11: Key Vault reference of the PEM encoded (PKCS#8) ECDSA/RSA PRIVATE key that signs every anchor
    /// (use a KMS/HSM backed secret provider; a custom <c>IAuditAnchorSigner</c> registered in DI takes precedence).
    /// Empty = anchors only carry the HMAC signature.
    /// </summary>
    public string ChainAnchorSignerKeyVaultRef { get; init; } = string.Empty;

    /// <summary>
    /// SEC E-11: Optional Key Vault reference of the PEM encoded PUBLIC key used to verify anchors (verify-only
    /// replicas). Empty = the public key is derived from the signing key.
    /// </summary>
    public string ChainAnchorVerifyKeyVaultRef { get; init; } = string.Empty;

    /// <summary>
    /// SEC R2-3: When true, query audit events (TABLE_QUERY/WEBSQL_QUERY ALLOW) are committed synchronously before the
    /// request is answered instead of through the in-memory channel, so a crash cannot lose them (lower throughput).
    /// </summary>
    public bool SynchronousQueryAudit { get; init; }

    /// <summary>SEC R2-3: Capacity of the asynchronous query audit channel = upper bound of entries lost on a crash.</summary>
    [Range(1, 100000)] public int QueryAuditChannelCapacity { get; init; } = 5000;

    /// <summary>AU-03: Indicates whether the audit HMAC key was resolved from fallback or default values.</summary>
    public bool HmacKeyIsFallback { get; set; }
}

/// <summary>
/// Diagnostics and logging options for generated SQL queries.
/// </summary>
public sealed class LoggingOptions
{
    public bool LogGeneratedSql { get; set; } = false;
}
