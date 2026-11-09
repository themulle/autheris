namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;

/// <summary>
/// One place that selects the post-validation token policy by the token issuer, for the HTTP bearer events and the WebSocket validator:
/// Entra tokens are checked against <see cref="EntraTokenPolicy"/>, AD FS tokens against <see cref="AdfsTokenPolicy"/>, never crosswise.
/// </summary>
public static class TokenPolicySelector
{
    public static IReadOnlySet<string> EntraIssuers(EntraIdAuthOptions entra)
    {
        var issuers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(entra.TenantId))
        {
            var instance = string.IsNullOrWhiteSpace(entra.Instance)
                ? "https://login.microsoftonline.com/"
                : entra.Instance.TrimEnd('/') + "/";
            issuers.Add($"{instance}{entra.TenantId}/v2.0");
            issuers.Add($"https://sts.windows.net/{entra.TenantId}/");
        }

        return issuers;
    }

    public static IReadOnlySet<string> AdfsIssuers(AdfsAuthOptions adfs)
    {
        var issuers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(adfs.Authority))
        {
            issuers.Add(adfs.Authority.TrimEnd('/'));
            issuers.Add($"{adfs.Authority.TrimEnd('/')}/services/trust");
        }

        return issuers;
    }

    /// <returns>A failure reason when the token must be rejected, otherwise <c>null</c>.</returns>
    public static string? Apply(ClaimsPrincipal principal, string? issuer, AuthenticationOptions auth, IIdentitySubjectResolver subjectResolver)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(subjectResolver);
        var value = issuer ?? string.Empty;
        if (auth.EntraId.Enabled && EntraIssuers(auth.EntraId).Contains(value))
        {
            return EntraTokenPolicy.Apply(principal, auth.EntraId, subjectResolver);
        }

        if (auth.Adfs.Enabled && AdfsIssuers(auth.Adfs).Contains(value))
        {
            return AdfsTokenPolicy.Apply(principal, auth.Adfs);
        }

        // With an identity provider enabled, a token from any other issuer is not accepted (the HTTP bearer events validate the issuer set
        // before this point; this keeps the socket path closed even when its key material is shared).
        return auth.EntraId.Enabled || auth.Adfs.Enabled ? "The token issuer is not a configured identity provider." : null;
    }
}
