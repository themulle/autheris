using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Http;

namespace Autheris.Api.Security;

/// <summary>Chooses the authentication scheme of a request (the forward selector of the gateway policy scheme).</summary>
internal static class GatewayAuthSchemeSelector
{
    public const string AmbiguousIdentityReason = "auth.ambiguous_identity";

    /// <summary><see cref="HttpContext.Items"/> key under which the selector hands a fixed reject reason to the reject handler.</summary>
    public const string RejectReasonItemKey = "Autheris.Auth.RejectReason";

    public static string Select(
        HttpContext context,
        GatewayOptions gatewayOptions,
        bool isBasicSessionAllowed,
        string basicSessionCookieName,
        bool isDevAuthAllowed)
    {

        var authHeader = context.Request.Headers.Authorization.ToString();

        // 1. Explicit Authorization headers have top priority (prevents ForwardAuth Header-Preemption DoS)
        // the opaque SCIM secret is not a JWT; route it to the Scim scheme so valid provisioning calls are authenticated
        // before the unauthenticated-bearer IP budget and do not produce JwtBearer failures.
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && GatewayAuthSchemes.IsScimPath(context.Request.Path))
        {
            return GatewayAuthSchemes.Scim;
        }

        // The mode comes from configuration only. Direct never selects ForwardAuth; Upstream ignores the Authorization
        // header for user identity; Both refuses a request that carries a credential and identity headers (never mixed).
        var forwardAuth = gatewayOptions.Authentication.ForwardAuth;
        var mode = gatewayOptions.Authentication.EffectiveMode;
        if (forwardAuth.Enabled && mode == AuthenticationMode.Upstream)
        {
            return GatewayAuthSchemes.ForwardAuth;
        }

        if (forwardAuth.Enabled && mode == AuthenticationMode.Both)
        {
            var headers = context.Request.Headers;
            var hasIdentityHeader = false;
            foreach (var name in forwardAuth.IdentityHeaderNames)
            {
                if (headers.ContainsKey(name))
                {
                    hasIdentityHeader = true;
                    break;
                }
            }

            if (hasIdentityHeader && !string.IsNullOrWhiteSpace(authHeader))
            {
                context.Items[RejectReasonItemKey] = AmbiguousIdentityReason;
                return GatewayAuthSchemes.RejectScheme;
            }

            if (hasIdentityHeader && !string.IsNullOrWhiteSpace(forwardAuth.UserHeader) && headers.ContainsKey(forwardAuth.UserHeader))
            {
                return GatewayAuthSchemes.ForwardAuth;
            }
        }

        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayAuthSchemes.SelectJwtScheme(authHeader["Bearer ".Length..], gatewayOptions);
        }

        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            if (gatewayOptions.Authentication.RequireKerberosOnly)
            {
                return gatewayOptions.Authentication.IsKerberosConfigured
                    ? NegotiateDefaults.AuthenticationScheme
                    : GatewayAuthSchemes.JwtBearer;
            }
            if (gatewayOptions.Authentication.BasicAuth.Enabled)
            {
                return GatewayAuthSchemes.Basic;
            }
            return gatewayOptions.Authentication.IsKerberosConfigured
                ? NegotiateDefaults.AuthenticationScheme
                : GatewayAuthSchemes.JwtBearer;
        }

        if (authHeader.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase))
        {
            return gatewayOptions.Authentication.IsKerberosConfigured
                ? NegotiateDefaults.AuthenticationScheme
                : GatewayAuthSchemes.JwtBearer;
        }

        if (!gatewayOptions.Authentication.RequireKerberosOnly &&
            authHeader.StartsWith("NTLM ", StringComparison.OrdinalIgnoreCase))
        {
            return gatewayOptions.Authentication.IsKerberosConfigured
                ? NegotiateDefaults.AuthenticationScheme
                : GatewayAuthSchemes.JwtBearer;
        }

        // 2b. F-AUTH-DX: session cookie (explicit Authorization headers above always win)
        if (isBasicSessionAllowed && context.Request.Cookies.ContainsKey(basicSessionCookieName))
        {
            return BasicAuthSession.SchemeName;
        }

        // 3. Development Test Auth Simulation or Insecure Anonymous Access
#if AUTHERIS_TEST_AUTH
        if (isDevAuthAllowed)
        {
            if (context.Request.Headers.ContainsKey("X-Test-User-Sid") ||
                context.Request.Headers.ContainsKey("X-Test-AppId") ||
                gatewayOptions.IsAnonymousAccessAllowed)
            {
                return TestAuthHandler.SchemeName;
            }
        }
#endif

        var isHtml = context.Request.Headers.Accept.Any(a => a != null && a.Contains("text/html", StringComparison.OrdinalIgnoreCase));
        if (isHtml && gatewayOptions.Authentication.BasicAuth.Enabled)
        {
            // Browser request: Prefer Basic challenge when enabled so browser opens native dialog
            return GatewayAuthSchemes.Basic;
        }

        // 4. Fallback challenge when unauthenticated
        if (gatewayOptions.Authentication.BasicAuth.Enabled &&
            !gatewayOptions.Authentication.RequireKerberosOnly &&
            string.IsNullOrWhiteSpace(authHeader))
        {
            return GatewayAuthSchemes.Basic;
        }

        if (gatewayOptions.Authentication.IsKerberosConfigured)
        {
            return NegotiateDefaults.AuthenticationScheme;
        }

        return GatewayAuthSchemes.JwtBearer;
    }
}
