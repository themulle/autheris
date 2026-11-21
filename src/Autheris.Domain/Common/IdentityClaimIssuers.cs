namespace Autheris.Domain.Common;

using System.Security.Claims;

/// <summary>Claim issuers stamped by Autheris itself. A claim carrying one of them was minted by the gateway, never by a token or a header.</summary>
public static class IdentityClaimIssuers
{
    /// <summary>Issuer of every group SID claim that <c>GetGroupSids</c> accepts.</summary>
    public const string Pipeline = "Autheris:IdentityPipeline";

    /// <summary>Issuer tag for a <see cref="ClaimTypes.NameIdentifier"/> synthesized from a non-<c>sub</c> claim during normalization.</summary>
    public const string FallbackNameIdentifier = "Autheris:ClaimsNormalizer:FallbackNameIdentifier";

    /// <summary>Issuer tag for a <see cref="ClaimTypes.PrimarySid"/> synthesized during normalization from a non-SID subject claim.</summary>
    public const string SynthesizedPrimarySid = "autheris:synthesized";

    /// <summary>The only way to mint a <see cref="ClaimTypes.GroupSid"/> claim that <c>GetGroupSids</c> reads.</summary>
    public static Claim CreateGroupSidClaim(string sid) => new(ClaimTypes.GroupSid, sid, ClaimValueTypes.String, Pipeline);

    /// <summary>Mints a realm-bound <see cref="ClaimTypes.PrimarySid"/> claim for non-Windows realms.</summary>
    public static Claim CreatePrimarySidClaim(string sid) => new(ClaimTypes.PrimarySid, sid, ClaimValueTypes.String, Pipeline);
}
