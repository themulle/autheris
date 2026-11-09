namespace Autheris.Domain.Common;

using System.Security.Claims;

/// <summary>Claim issuers stamped by Autheris itself. A claim carrying one of them was minted by the gateway, never by a token or a header.</summary>
public static class IdentityClaimIssuers
{
    /// <summary>Issuer of every group SID claim that <c>GetGroupSids</c> accepts.</summary>
    public const string Pipeline = "Autheris:IdentityPipeline";

    /// <summary>The only way to mint a <see cref="ClaimTypes.GroupSid"/> claim that <c>GetGroupSids</c> reads.</summary>
    public static Claim CreateGroupSidClaim(string sid) => new(ClaimTypes.GroupSid, sid, ClaimValueTypes.String, Pipeline);
}
