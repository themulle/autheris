namespace Autheris.Infrastructure.Databricks;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Connectors;
using Autheris.Domain.Options;

/// <summary>
/// Issue #238 (PLAN-DATA-238, WP-SC-15): Contract for acquiring execution credentials for Databricks.
/// Supports OAuth M2M, PAT, and OAuth 2.0 On-Behalf-Of (OBO) token exchange.
/// </summary>
public interface IDatabricksCredentialProvider
{
    Task<string> AcquireTokenAsync(DatabricksDataSourceOptions db, DelegationContext? context, CancellationToken ct);
}
