using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Domain.Options;

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
    /// When enabled, manual changes to structured virtual filters and access profiles require four-eyes approval by a distinct approver.
    /// Stage-2 SQL virtual filters (sql) always start PendingApproval and require four-eyes approval by a distinct owner or delegate of every referenced table (or sync-path activation), even when RequireApproval is false.
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
/// Issue #129 (129 C-4):<c>Gateway:CallerAttributes</c>. With <see cref="Enabled"/> false (default) nothing changes: a
/// <c>USER_ATTRIBUTE</c> consent leaf and a <c>{{caller.x}}</c> placeholder stay constants (no rows), and definitions are refused.
/// </summary>
public sealed class CallerAttributeOptions
{
    public const int MaxValuesHardCapCeiling = 1000;
    public const int TtlCeilingSeconds = 300;

    /// <summary>Enables dynamic caller attribute resolution for <c>USER_ATTRIBUTE</c> consents and <c>{{caller.*}}</c> placeholders.</summary>
    public bool Enabled { get; init; }

    [Range(1, MaxValuesHardCapCeiling)] public int MaxValuesHardCap { get; init; } = 200;

    [Range(1, TtlCeilingSeconds)] public int DefaultTtlSeconds { get; init; } = 60;

    /// <summary>Hard upper bound of every attribute TTL; never above 300 s (the bound for table and service-set providers).</summary>
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

/// <summary>An issuer whose validated bearer tokens may supply claims; <see cref="SubjectKeyIsPerson"/> allows <c>sub</c>as a person key.</summary>
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
