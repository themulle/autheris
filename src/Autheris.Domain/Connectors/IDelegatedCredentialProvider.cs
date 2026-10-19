namespace Autheris.Domain.Connectors;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Issue #238 (PLAN-DATA-238, WP-SC-15): Context required to acquire downstream delegated credentials.
/// </summary>
public sealed record DelegationContext(
    string? CallerToken = null,
    ClaimsPrincipal? Principal = null,
    TenantId? TenantId = null,
    string? CorrelationId = null,
    string? TraceId = null);

/// <summary>
/// Issue #238 (PLAN-DATA-238, FR-238-1.2): Generic provider returning connector-specific execution credentials based on DelegationContext.
/// </summary>
public interface IDelegatedCredentialProvider
{
    Task<string> GetCredentialAsync(string dataSourceName, DelegationContext context, CancellationToken ct);
}
