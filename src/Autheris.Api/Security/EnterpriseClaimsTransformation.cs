using System.Security.Claims;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Authentication;

namespace Autheris.Api.Security;

public sealed class EnterpriseClaimsTransformation : IClaimsTransformation
{
    internal const string TransformedMarkerClaimType = ClaimsNormalizer.LegacyMarkerClaimType;

    /// <summary>RR-L2-04: Issuer of the marker claim. Claims from tokens carry the token issuer and are never trusted.</summary>
    internal const string MarkerIssuer = ClaimsNormalizer.LegacyMarkerIssuer;

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        return Task.FromResult(ClaimsNormalizer.Normalize(principal));
    }
}
