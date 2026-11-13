namespace Autheris.Domain.Security;

/// <summary>
/// Value object capturing caller application provenance for audit logs and FinOps attribution.
/// Supplied via optional request headers (X-Caller-Application, X-Caller-Version, X-Caller-Query-Name).
/// </summary>
public sealed record CallerContext(string? Application = null, string? Version = null, string? QueryName = null, string? ClientId = null)
{
    /// <summary>True when any label came from a request header: the client says so itself and nothing authorizes on it. <see cref="ClientId"/> is token-derived only.</summary>
    public bool IsClientAsserted { get; init; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Application) &&
                           string.IsNullOrWhiteSpace(Version) &&
                           string.IsNullOrWhiteSpace(QueryName) &&
                           string.IsNullOrWhiteSpace(ClientId);
}
