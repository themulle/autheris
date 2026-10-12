namespace Autheris.Domain.Security;

/// <summary>
/// Value object capturing caller application provenance for audit logs and FinOps attribution.
/// Supplied via optional request headers (X-Caller-Application, X-Caller-Version, X-Caller-Query-Name).
/// </summary>
public sealed record CallerContext(string? Application = null, string? Version = null, string? QueryName = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Application) &&
                           string.IsNullOrWhiteSpace(Version) &&
                           string.IsNullOrWhiteSpace(QueryName);
}
