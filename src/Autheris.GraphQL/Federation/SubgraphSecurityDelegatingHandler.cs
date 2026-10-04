namespace Autheris.GraphQL.Federation;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Federation.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// HTTP DelegatingHandler attached to federated Subgraph HTTP clients.
/// Enforces Zero-Trust security context propagation and SSRF protection on every outbound subgraph query.
/// </summary>
public sealed class SubgraphSecurityDelegatingHandler : DelegatingHandler
{
    private readonly string _subgraphName;
    private readonly ISubgraphContextPropagationService _propagationService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<SubgraphSecurityDelegatingHandler> _logger;
    private readonly bool _isDev;

    public SubgraphSecurityDelegatingHandler(
        string subgraphName,
        ISubgraphContextPropagationService propagationService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<SubgraphSecurityDelegatingHandler> logger,
        IOptions<GatewayOptions>? options = null)
    {
        _subgraphName = subgraphName ?? throw new ArgumentNullException(nameof(subgraphName));
        _propagationService = propagationService ?? throw new ArgumentNullException(nameof(propagationService));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Relaxed SSRF validation only while DANGER bypasses are active (Development-only by startup validation).
        // WARN entries are permitted in Production and therefore must not relax the destination check.
        _isDev = options?.Value.HasAnyDangerBypassActive == true;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri != null)
        {
            await DeclarativeHttpDataSourceExecutor.ValidateDestinationUrlAsync(request.RequestUri, _isDev, cancellationToken).ConfigureAwait(false);
        }

        var httpContext = _httpContextAccessor.HttpContext;
        var principal = httpContext?.User;

        string? tenantId = null;
        if (httpContext?.Items.TryGetValue("TenantId", out var tObj) == true && tObj != null)
        {
            tenantId = tObj switch
            {
                Autheris.Domain.Common.TenantId tid => tid.Value,
                string tStr => tStr,
                _ => tObj.ToString()
            };
        }

        // Apply Zero-Trust Security headers (Subject SID, Tenant, Roles) & SSRF check
        _propagationService.ApplySecurityHeaders(request, _subgraphName, principal, tenantId);

        // Forward Authorization Bearer token downstream ONLY (NEVER forward Basic credentials to prevent confused deputy credential leaks)
        if (httpContext?.Request.Headers.TryGetValue("Authorization", out var authVals) == true && authVals.Count > 0)
        {
            var firstAuth = authVals[0];
            if (firstAuth != null && firstAuth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                if (!request.Headers.Contains("Authorization"))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", firstAuth);
                }
            }
        }

        _logger.LogDebug("Dispatching federated query to Subgraph '{Subgraph}' at {Uri}",
            _subgraphName, request.RequestUri);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
