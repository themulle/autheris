namespace Autheris.Application.Federation.Services;

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Claims;
using Autheris.Application.Federation.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Application.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class SubgraphContextPropagationService : ISubgraphContextPropagationService
{
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<SubgraphContextPropagationService> _logger;
    private readonly IHostEnvironment? _environment;

    public SubgraphContextPropagationService(
        IOptions<GatewayOptions> options,
        ILogger<SubgraphContextPropagationService> logger,
        IHostEnvironment? environment = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = environment;
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
        string? userSid = null;
        string rolesStr = string.Empty;
        if (principal != null)
        {
            userSid = principal.GetUserSid()?.Value
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
                rolesStr = string.Join(",", roles.OrderBy(r => r, StringComparer.Ordinal));
                request.Headers.Remove(fedOptions.RolesHeaderName);
                request.Headers.TryAddWithoutValidation(fedOptions.RolesHeaderName, rolesStr);
            }
        }

        // 4. Propagate Tenant ID
        // DT-A-3: the tenant comes from the resolved request tenant or the stamped claim; no sentinel. Without one no tenant header is
        // forwarded (the subgraph then rejects the call fail-closed).
        var claimTenant = Autheris.Domain.Tenancy.TenantClaimReader.Read(principal);
        var effectiveTenant = tenantId
            ?? (claimTenant.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid ? claimTenant.Tenant.Value : null);

        if (!string.IsNullOrWhiteSpace(effectiveTenant))
        {
            request.Headers.Remove(fedOptions.TenantHeaderName);
            request.Headers.TryAddWithoutValidation(fedOptions.TenantHeaderName, effectiveTenant);
        }

        // 5. Propagate Correlation ID for W3C distributed tracing
        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        request.Headers.Remove("X-Correlation-ID");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);

        // 6. Sign Zero-Trust Context Headers with HMAC-SHA256 (1.10 / SG-21)
        if (fedOptions.SignContextHeaders)
        {
            var isDevelopment = _environment == null || _environment.IsDevelopment();
            string? signingKey = fedOptions.SigningKey;

            if (string.IsNullOrWhiteSpace(signingKey) || (!isDevelopment && string.Equals(signingKey, "autheris-federation-default-secret", StringComparison.OrdinalIgnoreCase)))
            {
                if (isDevelopment)
                {
                    signingKey = !string.IsNullOrWhiteSpace(fedOptions.SigningKey)
                        ? fedOptions.SigningKey
                        : (_options.Value.DataMasking?.HmacSecretKeyVaultRef ?? "autheris-federation-default-secret");
                }
                else
                {
                    throw new InvalidOperationException(
                        "Security error: Federation Zero-Trust context header signing is enabled (Gateway:Federation:SignContextHeaders = true), " +
                        "but no secure SigningKey is configured. Using the default secret is prohibited outside the Development environment.");
                }
            }

            if (!isDevelopment)
            {
                SecretKeyRequirements.EnsureMinimumLength(
                    System.Text.Encoding.UTF8.GetBytes(signingKey),
                    "The Federation context header signing key (Federation:SigningKey)",
                    isDevelopment: false);
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = Guid.NewGuid().ToString("N");
            var methodStr = request.Method.Method;
            // VF-09: the signed "path" is the path AND the query string (GraphQL GET requests carry query/variables there).
            var requestUri = request.RequestUri;
            var pathStr = requestUri == null
                ? string.Empty
                : requestUri.IsAbsoluteUri ? requestUri.PathAndQuery : requestUri.OriginalString;

            // SR-M-18: bind the request body via SHA-256 and sign an unambiguous length-prefixed v2 form.
            string bodyHash;
            if (request.Content == null)
            {
                bodyHash = FederationContextSignature.HashBody(ReadOnlySpan<byte>.Empty);
            }
            else
            {
                using var bodyStream = request.Content.ReadAsStream();
                bodyHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bodyStream));
                if (bodyStream.CanSeek)
                {
                    bodyStream.Position = 0;
                }
            }

            var payload = FederationContextSignature.BuildCanonicalV2(
                effectiveTenant ?? string.Empty, userSid, timestamp, nonce, subgraphName, rolesStr, methodStr, pathStr, bodyHash);
            var signature = FederationContextSignature.Sign(payload, signingKey);

            request.Headers.Remove(FederationContextSignature.VersionHeader);
            request.Headers.Remove(FederationContextSignature.BodyHashHeader);
            request.Headers.TryAddWithoutValidation(FederationContextSignature.VersionHeader, FederationContextSignature.CurrentVersion);
            request.Headers.TryAddWithoutValidation(FederationContextSignature.BodyHashHeader, bodyHash);
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
