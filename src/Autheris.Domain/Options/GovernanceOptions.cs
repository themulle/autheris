namespace Autheris.Domain.Options;

/// <summary>
/// Issue #240: Governance read-path and operation options (<c>Gateway:Governance</c>).
/// </summary>
public sealed class GovernanceOptions
{
    public const string SectionName = "Gateway:Governance";

    /// <summary>
    /// Configuration for static query analysis (<c>Gateway:Governance:QueryAnalysis</c>).
    /// </summary>
    public GovernanceQueryAnalysisOptions QueryAnalysis { get; init; } = new();

    /// <summary>
    /// Configuration for the external consent REST API (<c>Gateway:Governance:ExternalApi</c>).
    /// </summary>
    public ExternalApiSettings ExternalApi { get; init; } = new();
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
/// Rate limit budgets (requests per minute per authenticated principal SID) for the consent REST API.
/// </summary>
public sealed class ExternalApiRateLimitSettings
{
    /// <summary>
    /// Maximum read requests per minute allowed per authenticated principal SID on consent REST endpoints.
    /// </summary>
    public int ReadsPerMinute { get; init; } = 600;

    /// <summary>
    /// Maximum decision and revocation write requests per minute allowed per authenticated principal SID on consent REST endpoints.
    /// </summary>
    public int DecisionsAndRevokesPerMinute { get; init; } = 60;

    /// <summary>
    /// Maximum cryptographic grant-proof inspection requests per minute allowed per authenticated principal SID.
    /// </summary>
    public int ProofPerMinute { get; init; } = 120;
}

/// <summary>
/// Issue #240: Switches and configuration for the query analysis endpoint (<c>Gateway:Governance:QueryAnalysis</c>).
/// </summary>
public sealed class GovernanceQueryAnalysisOptions
{
    public const string SectionName = "Gateway:Governance:QueryAnalysis";

    /// <summary>
    /// When true (default), the <c>POST /api/v1/governance/queries/analyze</c> endpoint is mapped and operational.
    /// When false, the endpoint is not mapped, returning HTTP 404 Not Found.
    /// </summary>
    public bool Enabled { get; init; } = true;
}
