namespace Autheris.Infrastructure.Databricks;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Connectors;
using Autheris.Domain.Options;

/// <summary>
/// Issue #238: Contract for acquiring execution credentials for Databricks.
/// Supports OAuth M2M, PAT, and OAuth 2.0 On-Behalf-Of (OBO) token exchange.
/// </summary>
public interface IDatabricksCredentialProvider
{
    Task<string> AcquireTokenAsync(DatabricksDataSourceOptions db, DelegationContext? context, CancellationToken ct);

    /// <summary>
    /// Evicts the cached token that <see cref="AcquireTokenAsync"/> would use for this caller after the
    /// downstream rejected it (HTTP 401/403). Never purges by client id or host beyond the caller's own entry; never throws.
    /// </summary>
    void EvictToken(DatabricksDataSourceOptions db, DelegationContext? context);

    /// <summary>
    /// Evicts cached credentials or tokens for the data source and optional tenant so that the next acquire re-reads the secret.
    /// </summary>
    void InvalidateSecret(DatabricksDataSourceOptions db, string? tenantId = null);
}
