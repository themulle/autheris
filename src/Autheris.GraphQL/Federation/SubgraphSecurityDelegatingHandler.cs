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
    private readonly Autheris.Application.Federation.Interfaces.ISubgraphCanaryRouter? _canaryRouter;
    private readonly bool _isDev;

    public SubgraphSecurityDelegatingHandler(
        string subgraphName,
        ISubgraphContextPropagationService propagationService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<SubgraphSecurityDelegatingHandler> logger,
        IOptions<GatewayOptions>? options = null,
        Autheris.Application.Federation.Interfaces.ISubgraphCanaryRouter? canaryRouter = null)
    {
        _subgraphName = subgraphName ?? throw new ArgumentNullException(nameof(subgraphName));
        _propagationService = propagationService ?? throw new ArgumentNullException(nameof(propagationService));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _canaryRouter = canaryRouter;
        // Relaxed SSRF validation only while DANGER bypasses are active (Development-only by startup validation).
        // WARN entries are permitted in Production and therefore must not relax the destination check.
        _isDev = options?.Value.HasAnyDangerBypassActive == true;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
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

        string? variantHeader = null;
        if (request.Headers.TryGetValues("X-Feature-Variant", out var vals))
        {
            variantHeader = System.Linq.Enumerable.FirstOrDefault(vals);
        }
        else if (httpContext?.Request.Headers.TryGetValue("X-Feature-Variant", out var hVal) == true)
        {
            variantHeader = hVal.ToString();
        }

        // F-OPS-03: Dynamic feature flagging and canary traffic splitting
        if (request.RequestUri != null && _canaryRouter != null)
        {
            var decision = await _canaryRouter.ResolveTargetAsync(_subgraphName, request.RequestUri, principal, tenantId, variantHeader, cancellationToken).ConfigureAwait(false);
            if (decision.IsCanary)
            {
                request.RequestUri = decision.EffectiveUri;
                request.Headers.TryAddWithoutValidation("X-Subgraph-Variant", decision.VariantName);
                _logger.LogInformation("F-OPS-03 Canary routing applied: Subgraph '{Subgraph}' -> Variant '{Variant}' ({Uri})",
                    _subgraphName, decision.VariantName, decision.EffectiveUri);
            }
        }

        if (request.RequestUri != null)
        {
            await DeclarativeHttpDataSourceExecutor.ValidateDestinationUrlAsync(request.RequestUri, _isDev, cancellationToken).ConfigureAwait(false);
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
