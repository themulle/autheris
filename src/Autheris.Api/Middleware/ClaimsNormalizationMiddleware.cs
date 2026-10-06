namespace Autheris.Api.Middleware;

using System;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Microsoft.AspNetCore.Http;

/// <summary>
/// ADR-017 / K-K10: Ingress claims normalization middleware.
/// Executes directly after authentication to canonicalize Active Directory Windows-SIDs,
/// OIDC/OAuth2 claims, and mTLS client certificates into strongly-typed claims before authorization policies evaluate.
/// </summary>
public sealed class ClaimsNormalizationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User?.Identity?.IsAuthenticated == true || context.Connection.ClientCertificate != null)
        {
            var clientCert = context.Connection.ClientCertificate;
            context.User = ClaimsNormalizer.Normalize(context.User ?? new System.Security.Claims.ClaimsPrincipal(), clientCert);
        }

        await next(context).ConfigureAwait(false);
    }
}
