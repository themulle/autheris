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
