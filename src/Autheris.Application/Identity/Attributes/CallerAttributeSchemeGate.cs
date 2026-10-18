namespace Autheris.Application.Identity.Attributes;

using System;
using System.Linq;
using System.Security.Claims;
using Autheris.Domain.Options;

/// <summary>
/// The authentication-scheme gate of caller attributes (PLAN-GOV-129 BC-129-01, F-6). The MCP session rebuilds its caller as an <c>McpAuth</c>
/// identity and loses the original scheme, so that identity passes the gate only with a gateway marker claim, which the gateway sets only when
/// the original identity was of an allowed scheme. Any marker claim a client supplies is stripped on entry.
/// </summary>
public static class CallerAttributeSchemeGate
{
    /// <summary>Authentication type of the identity the gateway rebuilds for an MCP session.</summary>
    public const string McpAuthScheme = "McpAuth";

    /// <summary>Gateway-only marker claim; honoured only on an <see cref="McpAuthScheme"/> identity.</summary>
    public const string MarkerClaimType = "autheris_attr_scheme_ok";

    /// <summary>True when an authenticated identity of the principal has an allowed authentication scheme (the marker is not consulted).</summary>
    public static bool HasAllowedOriginalScheme(ClaimsPrincipal principal, CallerAttributeOptions options)
    {
        var allowed = options.Claims.AllowedSchemes;
        foreach (var identity in principal.Identities)
        {
            if (identity.IsAuthenticated && !string.IsNullOrEmpty(identity.AuthenticationType) && allowed.Contains(identity.AuthenticationType, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool Passes(ClaimsIdentity identity, CallerAttributeOptions options)
    {
        if (!identity.IsAuthenticated || string.IsNullOrEmpty(identity.AuthenticationType))
        {
            return false;
        }

        return string.Equals(identity.AuthenticationType, McpAuthScheme, StringComparison.OrdinalIgnoreCase)
            ? identity.HasClaim(MarkerClaimType, "1")
            : options.Claims.AllowedSchemes.Contains(identity.AuthenticationType, StringComparer.OrdinalIgnoreCase);
    }
}
