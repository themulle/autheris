using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>Audit log retention and purge worker settings (<c>Gateway:Audit:Retention</c>).</summary>
public sealed class AuditRetentionOptions
{
    public const string SectionName = GatewaySections.AuditRetention;

    /// <summary>Enables periodic audit log retention enforcement and purging.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Default retention period in days for audit entries without a category-specific override.</summary>
    public int RetentionDays { get; init; } = 90;

    /// <summary>Retention period in days for security and administrative (Tier-A) audit events.</summary>
    public int SecurityDays { get; init; } = 3650;

    /// <summary>Retention period in days for data-access (Tier-B) audit events.</summary>
    public int DataAccessDays { get; init; } = 400;

    /// <summary>Retention period in days for aggregated audit summary records.</summary>
    public int AggregatedDays { get; init; } = 90;

    /// <summary>Retention period in days for operational and diagnostic audit events.</summary>
    public int OperationsDays { get; init; } = 30;

    /// <summary>Maximum number of expired audit rows deleted per purge batch.</summary>
    public int BatchSize { get; init; } = 5000;

    /// <summary>Interval between scheduled audit retention purge runs.</summary>
    public TimeSpan PurgeInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Distributed lease timeout for the audit retention purge job.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Delay between consecutive delete batches to limit database write contention.</summary>
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

    [Range(365, 7300)] public int AuditLogRetentionDays { get; init; } = 3650;

    /// <summary>Audit log retention and purge worker settings (<c>Gateway:Audit:Retention</c>).</summary>
    public AuditRetentionOptions Retention { get; init; } = new();

    /// <summary>
    /// 23 (X1.2): store the compiled SQL text (markers only, no values by construction) in the audit record of a governed
    /// statement. Default <c>false</c>: the record carries only the compiler version, the dialect, the bind count and the
    /// <c>sql.compiled_digest</c>.
    /// </summary>
    public bool StoreCompiledSql { get; init; }
    [Range(1, 168)] public int VerifyHashChainIntervalHours { get; init; } = 24;

    /// <summary>Review E-11: verify the audit hash chain periodically at runtime (first run shortly after start).</summary>
    public bool VerifyHashChainEnabled { get; init; } = true;

    /// <summary>Review E-11: a broken audit hash chain turns the readiness probe unhealthy (set false to only log it).</summary>
    public bool FailReadinessOnChainViolation { get; init; } = true;

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
    /// <summary>When true, logs compiled SQL statements emitted by the query engine.</summary>
    public bool LogGeneratedSql { get; set; } = false;

    /// <summary>Controls whether SQL statement text may be included in diagnostic and audit log entries.</summary>
    public bool AllowStatementText { get; set; } = true;
}
