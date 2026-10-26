using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Domain.Options;

public sealed class CasbinOptions
{
    public bool Enabled { get; init; } = false;
    public string? ModelPath { get; init; }
    public string? PolicyPath { get; init; }
    public bool WatchPolicyFile { get; init; } = true;
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
    [Range(0, 5000)] public int GenerationCacheMilliseconds { get; init; } = 1000;
    public bool EnforceOnStreaming { get; init; } = false;

    /// <summary>
    /// INF-15: Maximum number of entries in the per-check memo during graph traversal recursion.
    /// </summary>
    [Range(1, 10_000)]
    public int PerCheckMemoMaxEntries { get; init; } = 256;

    /// <summary>
    /// POL-6: also require the ReBAC relation <c>can_query</c> on OData, GraphQL, WebSQL and stored procedures.
    /// Off by default: the evaluator denies every table without tuples, so enable it only once tuples are maintained.
    /// The unified PDP (MCP-RAG, DuckDB OLAP) checks ReBAC whenever <see cref="Enabled"/> is true.
    /// </summary>
    public bool EnforceOnQueryPaths { get; init; } = false;

    /// <summary>
    /// SR15-23: When true, users with the <c>viewer</c> relation automatically inherit <c>can_query</c>.
    /// Defaults to true for backwards compatibility with POL-6 / OLAP / Arrow egress, but can be set to false
    /// to require explicit <c>can_query</c>, <c>editor</c>, or <c>owner</c> relations, preventing existing <c>viewer</c>
    /// tuples from automatically granting full analytical and raw egress capabilities.
    /// </summary>
    public bool InheritCanQueryFromViewer { get; init; } = true;
    public string? OpenFgaApiUrl { get; init; }
    public string? OpenFgaStoreId { get; init; }

    /// <summary>
    /// Initial relationship tuples seeded into the ReBAC store on startup (for Development, PoC, or testing).
    /// </summary>
    public List<RebacTuple> SeedTuples { get; init; } = [];
}

/// <summary>Virtual filters (docs/plans/2026-10-08-virtuelle-filter.md).</summary>
public sealed class VirtualFilterOptions
{
    /// <summary>
    /// A sync that removes more bindings than this (or all of them) is not applied without <c>force</c>: removing a
    /// binding widens what its grantee sees. 0 disables the check.
    /// </summary>
    public int MaxRemovals { get; init; } = 10;

    /// <summary>
    /// How often (seconds) an instance compares the virtual filter generation with the governance database; changes made
    /// on another instance take effect within this time. 0 compares on every access.
    /// </summary>
    public int GenerationCheckSeconds { get; init; } = 5;

    /// <summary>
    /// When enabled, manual changes to virtual filters and access profiles require four-eyes approval by a distinct approver.
    /// </summary>
    public bool RequireApproval { get; init; } = false;

    /// <summary>F-GOV-14: Virtual Filters on Web-API tables in WebSQL cross-source queries.</summary>
    public VirtualFilterFederationOptions Federation { get; init; } = new();

    /// <summary>
    /// SR15-11: Optional allowlist of table names or qualified identifiers allowed in virtual filter subqueries.
    /// When non-empty, any table referenced in a virtual filter subquery must be present in this list.
    /// </summary>
    public IReadOnlyList<string>? AllowedReferenceTables { get; init; } = null;

    /// <summary>
    /// SR15-15: Sliding time window in minutes across which removals and relaxations accumulate toward MaxRemovals.
    /// Default is 60 minutes.
    /// </summary>
    public int MaxRemovalsWindowMinutes { get; init; } = 60;

    /// <summary>
    /// HMAC-SHA256 secret used to validate GitHub webhook triggers for virtual filter GitOps sync (X-Hub-Signature-256).
    /// </summary>
    public string? WebhookSecret { get; init; }

    /// <summary>
    /// Configured Git reference (e.g. "refs/heads/main" or "refs/tags/v1.0") to accept push events for.
    /// Default is null (accepts any push when not configured).
    /// </summary>
    public string? GitRef { get; init; }
}

/// <summary>
/// Row-Level Security row filter generation strategy options.
/// </summary>
public sealed class RowFilterOptions
{
    public RowFilterSubqueryStrategy SubqueryStrategy { get; set; } = RowFilterSubqueryStrategy.Exists;

    /// <summary>Issue #107: key-set cache strategy for correlated row filters (<c>Gateway:RowFilters:KeySetCache</c>).</summary>
    public RowFilterKeySetCacheOptions KeySetCache { get; init; } = new();
}

/// <summary>
/// Issue #107 global defaults and caps of the key-set cache strategy. Ranges (validated at startup, runtime values above a lowered cap are clamped):
/// <c>1 s &lt;= DefaultDuration &lt;= MaxDuration &lt;= 24 h</c>, <c>1 &lt;= MaxKeys &lt;= 10,000</c>, <c>0 &lt;= BindParameterReserve &lt;= 1,000</c>,
/// <c>1 &lt;= MaxConcurrentKeyQueries &lt;= 64</c>, <c>SizeLimitBytes &gt; 0</c>, <c>KeyQueryTimeout &gt; 0</c>.
/// </summary>
public sealed class RowFilterKeySetCacheOptions
{
    public const int MaxKeysCeiling = 10_000;
    public const int BindParameterReserveCeiling = 1_000;
    public const int MaxConcurrentKeyQueriesCeiling = 64;
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DurationCeiling = TimeSpan.FromHours(24);

    public RowFilterEvaluationStrategy DefaultStrategy { get; init; } = RowFilterEvaluationStrategy.Exists;
    public TimeSpan DefaultDuration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromHours(1);
    public int MaxKeys { get; init; } = 2000;
    public long SizeLimitBytes { get; init; } = 64L * 1024 * 1024;
    public TimeSpan KeyQueryTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int BindParameterReserve { get; init; } = 256;
    /// <summary>SEC-RF107-13: concurrent key query loads per node.</summary>
    public int MaxConcurrentKeyQueries { get; init; } = 4;
}

/// <summary>
/// Issue #129 (PLAN-GOV-129 C-4): <c>Gateway:CallerAttributes</c>. With <see cref="Enabled"/> false (default) nothing changes: a
/// <c>USER_ATTRIBUTE</c> consent leaf and a <c>{{caller.x}}</c> placeholder stay constants (no rows), and definitions are refused.
/// </summary>
public sealed class CallerAttributeOptions
{
    public const int MaxValuesHardCapCeiling = 1000;
    public const int TtlCeilingSeconds = 300;

    public bool Enabled { get; init; }

    [Range(1, MaxValuesHardCapCeiling)] public int MaxValuesHardCap { get; init; } = 200;

    [Range(1, TtlCeilingSeconds)] public int DefaultTtlSeconds { get; init; } = 60;

    /// <summary>Hard upper bound of every attribute TTL; never above 300 s (SEC-129-04: the bound for table and service-set providers).</summary>
    [Range(1, TtlCeilingSeconds)] public int MaxTtlSeconds { get; init; } = TtlCeilingSeconds;

    /// <summary>Negative cache TTL of a <c>Missing</c> result (a re-provisioned person waits at most this long).</summary>
    [Range(1, TtlCeilingSeconds)] public int NegativeTtlSeconds { get; init; } = 15;

    [Range(100, 10_000)] public int ProviderTimeoutMilliseconds { get; init; } = 2000;

    /// <summary>Bulkhead per provider kind (BC-129-12); a rejected call is the generic <c>Failed</c> outcome.</summary>
    [Range(1, 256)] public int ProviderMaxConcurrency { get; init; } = 16;

    [Required] public CallerAttributeCircuitBreakerOptions CircuitBreaker { get; init; } = new();

    /// <summary>Per statement bind budget reserved for caller attribute expansion (BC-129-09); the sum over all attributes of a statement is checked against it.</summary>
    [Range(0, 5000)] public int BindReserve { get; init; } = 400;

    public bool PadListsToBuckets { get; init; } = true;

    /// <summary>The request memo is discarded after <c>min(attribute TTL, this)</c> seconds (BC-129-07).</summary>
    [Range(1, 300)] public int RequestMemoSeconds { get; init; } = 30;

    [Required] public CallerAttributeCacheOptions Cache { get; init; } = new();

    [Required] public CallerAttributeClaimsOptions Claims { get; init; } = new();

    /// <summary>BC-129-03: changes of attribute-table providers and service sets need an approver holding the separate approver role.</summary>
    public bool RequireSeparateApproverRole { get; init; } = true;

    /// <summary>Tables (<c>schema.table</c>, case-insensitive) that may be both an attribute source and a catalog table (explicit opt-in, A-10).</summary>
    public IReadOnlyList<string> AllowAttributeSourceInCatalog { get; init; } = [];

    /// <summary>BC-129-13: administrator requests per minute of the simulation endpoint.</summary>
    [Range(1, 600)] public int SimulationRequestsPerMinute { get; init; } = 30;

    /// <summary>
    /// BC-129-13: secret of the per-tenant peppered value hashes of the simulation output. Empty: a random process-wide pepper (hashes are then only
    /// comparable within one process lifetime, which is all a simulation needs).
    /// </summary>
    public string? SimulationHashPepper { get; init; }
}

public sealed class CallerAttributeCircuitBreakerOptions
{
    [Range(1, 100)] public int FailureThreshold { get; init; } = 5;

    [Range(1, 600)] public int OpenSeconds { get; init; } = 10;
}

public sealed class CallerAttributeCacheOptions
{
    [Range(1, int.MaxValue)] public long SizeLimitBytes { get; init; } = 8L * 1024 * 1024;
}

/// <summary>An issuer whose validated bearer tokens may supply claims; <see cref="SubjectKeyIsPerson"/> allows <c>sub</c> as a person key (SEC-129-01).</summary>
public sealed class CallerAttributeTrustedIssuer
{
    public string Issuer { get; init; } = string.Empty;

    public bool SubjectKeyIsPerson { get; init; }
}

public sealed class CallerAttributeClaimsOptions
{
    /// <summary>Empty disables the claims provider. Every issuer must also be a <c>ValidIssuers</c> entry of the bearer scheme (startup validation).</summary>
    public IReadOnlyList<CallerAttributeTrustedIssuer> TrustedIssuers { get; init; } = [];

    /// <summary>Authentication types of the identity that owns a claim. Default: the JwtBearer identity only (F-6); ForwardAuth, Basic and Test are off.</summary>
    /// <remarks>
    /// <c>McpAuth</c> is deliberately not listed: the MCP session re-creates its caller as <c>McpAuth</c> and loses the original scheme, so that
    /// identity passes the gate only with the gateway marker claim, which is set only when the original scheme is listed here (BC-129-01).
    /// </remarks>
    public IReadOnlyList<string> AllowedSchemes { get; init; } = ["AuthenticationTypes.Federation", "Bearer"];

    [Range(1, 256)] public int MaxClaimValueLength { get; init; } = 256;

    [Range(1, 16 * 1024)] public int MaxTotalClaimBytes { get; init; } = 16 * 1024;
}
