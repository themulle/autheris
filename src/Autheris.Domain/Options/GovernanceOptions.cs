using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>
/// Issue #240: Governance read-path and operation options (<c>Gateway:Governance</c>).
/// </summary>
public sealed class GovernanceOptions
{
    public const string SectionName = "Gateway:Governance";

    /// <summary>
    /// Configuration for the external consent REST API (<c>Gateway:Governance:ExternalApi</c>).
    /// </summary>
    public ExternalApiSettings ExternalApi { get; init; } = new();

    /// <summary>
    /// Deployment defaults and upper ceilings for tenant access policies (<c>Gateway:Governance:AccessPolicy</c>).
    /// </summary>
    public AccessPolicyOptions AccessPolicy { get; init; } = new();

    /// <summary>
    /// Data Governance outbound notification webhook configuration (<c>Gateway:Governance:Notifications</c>).
    /// </summary>
    public GovernanceNotificationOptions Notifications { get; init; } = new();
}

/// <summary>
/// Configuration settings for governance protection caching and cross-node epoch coherence (<c>Gateway:Governance:Cache</c>).
/// </summary>
public sealed class GovernanceCacheSettings
{
    public const string SectionName = "Gateway:Governance:Cache";

    /// <summary>
    /// Maximum time-to-live in seconds for positive cached protection snapshots (1 to 300).
    /// </summary>
    [Range(1, 300)]
    public int MaxTtlSeconds { get; init; } = 300;

    /// <summary>
    /// Maximum time-to-live in seconds for negative or unassigned cached protection lookups (1 to 60).
    /// </summary>
    [Range(1, 60)]
    public int NegativeTtlSeconds { get; init; } = 60;

    /// <summary>
    /// Interval in seconds between database governance epoch polls per tenant (1 to 60).
    /// </summary>
    [Range(1, 60)]
    public int EpochPollSeconds { get; init; } = 2;
}

/// <summary>
/// Configuration settings for static query analysis (<c>Gateway:Governance:QueryAnalysis</c>).
/// </summary>
public sealed class QueryAnalysisSettings
{
    public const string SectionName = GatewaySections.QueryAnalysis;

    /// <summary>
    /// Allowlist of read-only scope names that may be supplied via <c>targetScope</c> on
    /// <c>POST /api/v1/governance/queries/analyze</c>. Empty by default (no target scopes allowed).
    /// </summary>
    public List<string> AllowedTargetScopes { get; init; } = [];
}

/// <summary>
/// Configuration settings for the canonical <c>/api/v1/consent</c> REST endpoints.
/// </summary>
public sealed class ExternalApiSettings
{
    public const string SectionName = "Gateway:Governance:ExternalApi";

    /// <summary>
    /// Per-principal rate limits for the consent REST API.
    /// </summary>
    public ExternalApiRateLimitSettings RateLimits { get; init; } = new();
}

/// <summary>
/// Rate limit budgets (requests per minute, per tenant: per person (SID) and per app-only client id) for the consent REST API.
/// </summary>
public sealed class ExternalApiRateLimitSettings
{
    /// <summary>
    /// Maximum read requests per minute on consent REST endpoints, per person (SID) and per app-only client id.
    /// </summary>
    public int ReadsPerMinute { get; init; } = 600;

    /// <summary>
    /// Maximum decision and revocation write requests per minute on consent REST endpoints, per person (SID) and per app-only client id.
    /// </summary>
    public int DecisionsAndRevokesPerMinute { get; init; } = 60;

    /// <summary>
    /// Maximum cryptographic grant-proof inspection requests per minute, per person (SID) and per app-only client id.
    /// </summary>
    public int ProofPerMinute { get; init; } = 120;
}
