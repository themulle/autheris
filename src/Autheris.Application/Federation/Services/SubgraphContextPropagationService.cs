namespace Autheris.Application.Federation.Services;

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Claims;
using Autheris.Application.Federation.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class SubgraphContextPropagationService : ISubgraphContextPropagationService
{
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<SubgraphContextPropagationService> _logger;

    public SubgraphContextPropagationService(
        IOptions<GatewayOptions> options,
        ILogger<SubgraphContextPropagationService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void ApplySecurityHeaders(
        HttpRequestMessage request,
        string subgraphName,
        ClaimsPrincipal? principal,
        string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. SSRF & DNS Rebinding validation for destination subgraph URL
        if (request.RequestUri != null)
        {
            DeclarativeHttpDataSourceExecutor.ValidateUrl(request.RequestUri);
        }

        var fedOptions = _options.Value.Federation;
        if (!fedOptions.EnableZeroTrustContextForwarding)
        {
            return;
        }

        // 2. Propagate Caller Subject SID
        if (principal != null)
        {
            var userSid = principal.GetUserSid()?.Value
                          ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                          ?? principal.Identity?.Name;

            if (!string.IsNullOrWhiteSpace(userSid))
            {
                request.Headers.Remove(fedOptions.SubjectHeaderName);
                request.Headers.TryAddWithoutValidation(fedOptions.SubjectHeaderName, userSid);
            }

            // 3. Propagate Caller Roles
            var roles = principal.GetUserRoles();
            if (roles.Count > 0)
            {
                request.Headers.Remove(fedOptions.RolesHeaderName);
                request.Headers.TryAddWithoutValidation(fedOptions.RolesHeaderName, string.Join(",", roles));
            }
        }

        // 4. Propagate Tenant ID
        var effectiveTenant = tenantId
            ?? principal?.FindFirst("tenant_id")?.Value
            ?? principal?.FindFirst("tenant")?.Value
            ?? TenantId.LegacySingleTenant.Value;

        if (!string.IsNullOrWhiteSpace(effectiveTenant))
        {
            request.Headers.Remove(fedOptions.TenantHeaderName);
            request.Headers.TryAddWithoutValidation(fedOptions.TenantHeaderName, effectiveTenant);
        }

        // 5. Propagate Correlation ID for W3C distributed tracing
        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        request.Headers.Remove("X-Correlation-ID");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);

        // 6. Sign Zero-Trust Context Headers with HMAC-SHA256 (1.10)
        if (fedOptions.SignContextHeaders)
        {
            var signingKey = !string.IsNullOrWhiteSpace(fedOptions.SigningKey)
                ? fedOptions.SigningKey
                : (_options.Value.DataMasking?.HmacSecretKeyVaultRef ?? "autheris-federation-default-secret");

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = Guid.NewGuid().ToString("N");
            var userSid = principal?.GetUserSid()?.Value ?? string.Empty;

            var payload = $"{effectiveTenant}:{userSid}:{timestamp}:{nonce}";
            using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(signingKey));
            var signature = Convert.ToHexStringLower(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload)));

            request.Headers.Remove("X-Autheris-Signature");
            request.Headers.Remove("X-Autheris-Timestamp");
            request.Headers.Remove("X-Autheris-Nonce");

            request.Headers.TryAddWithoutValidation("X-Autheris-Signature", signature);
            request.Headers.TryAddWithoutValidation("X-Autheris-Timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("X-Autheris-Nonce", nonce);
        }

        _logger.LogDebug("Propagated Zero-Trust context to subgraph '{Subgraph}' (Tenant: {Tenant}, Correlation: {Correlation})",
            subgraphName, effectiveTenant, correlationId);
    }
}
