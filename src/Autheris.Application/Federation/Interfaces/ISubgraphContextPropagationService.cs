namespace Autheris.Application.Federation.Interfaces;

using System.Net.Http;
using System.Security.Claims;

/// <summary>
/// Propagates Zero-Trust caller context (User SID, Tenant, Roles, Correlation ID) to downstream federated subgraphs
/// while enforcing strict outbound security controls (SSRF protection).
/// </summary>
public interface ISubgraphContextPropagationService
{
    /// <summary>
    /// Applies security headers to an outbound HTTP request destined for a federated subgraph.
    /// </summary>
    void ApplySecurityHeaders(
        HttpRequestMessage request,
        string subgraphName,
        ClaimsPrincipal? principal,
        string? tenantId);
}
