namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Thin <see cref="IClaimsTransformation"/> adapter over <see cref="IIdentityPipeline"/>. ASP.NET invokes the transformation on every
/// authenticate call, so the result is cached per request and input principal: the pipeline (and with it the group resolution) runs
/// once per request, and an already processed principal is returned as is by the pipeline.
/// </summary>
public sealed class EnterpriseClaimsTransformation(IIdentityPipeline pipeline, IHttpContextAccessor? httpContextAccessor = null) : IClaimsTransformation
{
    private const string CacheItemKey = "Autheris.IdentityPipeline.Results";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true })
        {
            return Task.FromResult(principal);
        }

        var context = httpContextAccessor?.HttpContext;
        if (context is null)
        {
            return RunAsync(principal, null);
        }

        var cache = context.Items.TryGetValue(CacheItemKey, out var existing) && existing is Dictionary<ClaimsPrincipal, Task<ClaimsPrincipal>> found
            ? found
            : new Dictionary<ClaimsPrincipal, Task<ClaimsPrincipal>>(ReferenceEqualityComparer.Instance);
        context.Items[CacheItemKey] = cache;
        lock (cache)
        {
            if (!cache.TryGetValue(principal, out var task))
            {
                task = RunAsync(principal, context);
                cache[principal] = task;
            }

            return task;
        }
    }

    private async Task<ClaimsPrincipal> RunAsync(ClaimsPrincipal principal, HttpContext? context) =>
        await pipeline.ProcessAsync(principal, SchemeOf(principal), context?.Connection.ClientCertificate, context?.RequestAborted ?? default).ConfigureAwait(false);

    /// <summary>The authentication scheme of a principal, from its authentication type (handler identities) or its token issuer (JWT).</summary>
    internal static string SchemeOf(ClaimsPrincipal principal)
    {
        var type = principal.Identity?.AuthenticationType;
        switch (type)
        {
            case GatewayAuthSchemes.Basic or "BasicSession" or GatewayAuthSchemes.ForwardAuth or GatewayAuthSchemes.Scim:
                return type;
#if AUTHERIS_TEST_AUTH
            case TestAuthHandler.SchemeName:
                // The test handler mints pipeline-issuer group claims like the Basic handler does.
                return GatewayAuthSchemes.Basic;
#endif
            case "Negotiate" or "Kerberos" or "NTLM":
                return "Negotiate";
            default:
                return principal.FindFirst("iss") is not null ? GatewayAuthSchemes.JwtBearer : type ?? "unknown";
        }
    }
}
