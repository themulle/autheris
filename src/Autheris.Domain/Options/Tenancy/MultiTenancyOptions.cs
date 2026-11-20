using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>Decision B-1: handling of tenant ids that differ only in case.</summary>
public sealed class TenantIsolationOptions
{
    /// <summary>
    /// Default <c>false</c>: colliding tenants are denied per request (403 <c>TENANT_ID_COLLISION</c> plus an audit entry) and the
    /// start only logs a critical warning, so one bad tenant id does not take down the others. <c>true</c> refuses the start
    /// outside Development (strict mode).
    /// </summary>
    public bool StrictCollisionStartup { get; init; }

    /// <summary>CR-ADG-31 / WP-X0p: the tenant registry that detects collisions between tenants known only to the identity provider.</summary>
    public TenantRegistryOptions Registry { get; init; } = new();
}

/// <summary>
/// Tenant registry settings (<c>Gateway:TenantIsolation:Registry</c>). On by default since X1 (Phase 5 L6): the existence check precedes the
/// budget and an issuer-less token has a budget per authentication scheme. <c>Enabled = false</c> is the explicit opt-out; the legacy
/// configured-tenant guard stays active either way.
/// </summary>
public sealed class TenantRegistryOptions
{
    /// <summary>When <c>true</c> every authenticated request registers its tenant id; the registry being unavailable then denies the request (503).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Budget of new tenant registrations per identity provider issuer and window.</summary>
    [Range(1, 100_000)] public int MaxNewTenantsPerIssuerPerWindow { get; init; } = 20;

    [Range(1, 86_400)] public int WindowSeconds { get; init; } = 60;

    /// <summary>TTL of the in-memory positive cache; a collision recorded by another replica takes effect within this time.</summary>
    [Range(1, 3_600)] public int CacheTtlSeconds { get; init; } = 60;

    [Range(1, 1_000_000)] public int MaxCacheEntries { get; init; } = 10_000;

    [Range(1, 100_000)] public int MaxTrackedIssuers { get; init; } = 1_024;
}

/// <summary>
/// F-GOV-08: Dynamic Schema Contracts &amp; Tag-based Projection.
/// Allows slicing the supergraph into isolated contract views (@tag / @inaccessible) for partners, mobile, and web.
/// </summary>
public sealed class SchemaContractsOptions
{
    /// <summary>Enables dynamic tag-based schema contract projection.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Name of the default schema contract applied when a caller specifies none.</summary>
    public string DefaultContract { get; init; } = "default";

    /// <summary>Named schema contract definitions keyed by contract identifier.</summary>
    public Dictionary<string, SchemaContractDefinitionOptions> Contracts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Definition of a single schema contract projection.</summary>
public sealed class SchemaContractDefinitionOptions
{
    /// <summary>Schema tags explicitly included in this contract view.</summary>
    public List<string> IncludedTags { get; init; } = [];

    /// <summary>Schema tags excluded from this contract view.</summary>
    public List<string> ExcludedTags { get; init; } = [];

    /// <summary>When true, excludes fields marked <c>@inaccessible</c> from this contract view.</summary>
    public bool ExcludeInaccessible { get; init; } = true;

    /// <summary>Optional allowlist of table names exposed by this contract.</summary>
    public List<string> AllowedTables { get; init; } = [];
}

/// <summary>Server-side bounds of asynchronous query jobs. Client-supplied timeout and row count never exceed them (SR-H-05).</summary>
public sealed class AsyncJobOptions
{
    /// <summary>Configuration section the options are bound from (standalone and through <see cref="GatewayOptions.AsyncJobs"/>).</summary>
    public const string SectionName = "Gateway:AsyncJobs";

    /// <summary>Maximum number of jobs this node executes in parallel.</summary>
    public int MaxConcurrentJobs { get; init; } = 4;

    /// <summary>Maximum number of jobs of one tenant this node executes in parallel (at most <see cref="MaxConcurrentJobs"/>).</summary>
    public int MaxConcurrentJobsPerTenant { get; init; } = 2;

    /// <summary>Absolute directory for job result files; null uses ContentRoot/scratch/jobs (the Helm chart sets a path on its writable /tmp volume).</summary>
    public string? ScratchDirectory { get; init; }

    /// <summary>True when <see cref="ScratchDirectory"/> is a volume shared by all replicas, so any node can serve a result written by another.</summary>
    public bool SharedScratchDirectory { get; init; }

    /// <summary>Maximum number of queued plus running jobs of one tenant on this node; further submits are rejected (HTTP 429).</summary>
    public int MaxQueuedJobsPerTenant { get; init; } = 50;

    /// <summary>Maximum number of queued plus running jobs of one user (by SID/owner); further submits are rejected (HTTP 429).</summary>
    public int MaxActiveJobsPerUser { get; init; } = 10;

    /// <summary>Maximum total scratch bytes used by job result files; when exceeded, further submits are rejected (HTTP 429).</summary>
    public long MaxScratchBytes { get; init; } = 1_073_741_824; // 1 GB

    /// <summary>Timeout applied when the client supplies none (or a non-positive one).</summary>
    public int DefaultTimeoutSeconds { get; init; } = 300;

    /// <summary>Upper bound for a client-supplied timeout.</summary>
    public int MaxTimeoutSeconds { get; init; } = 900;

    /// <summary>How often finished jobs and their result files are purged.</summary>
    public int PurgeIntervalMinutes { get; init; } = 15;

    /// <summary>How long a finished job and its result file are kept.</summary>
    public int RetentionHours { get; init; } = 24;
}
