namespace Autheris.Tests.Unit.Security;

using System.Security.Claims;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed class ConsentIdentityModelSecurityTests
{
    [Fact]
    public void SG_04_GetAllUserSids_ExcludesMutableClaims_ToPreventConsentImpersonation()
    {
        // Arrange: A user whose name and preferred_username are set to another user's SID (impersonation attack)
        var victimSid = "S-1-5-21-VICTIM-999";
        var victimOid = "00000000-0000-0000-0000-000000000001";
        var attackerRealSub = "attacker-sub-123";
        var attackerRealOid = "attacker-oid-456";

        var claims = new List<Claim>
        {
            new("sub", attackerRealSub),
            new("oid", attackerRealOid),
            // Attacker puts victim's identifiers in mutable profile fields
            new(ClaimTypes.Name, victimSid),
            new("name", victimSid),
            new("preferred_username", victimOid),
            new("upn", "victim@company.com"),
            new(ClaimTypes.Upn, "victim@company.com")
        };

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var consentMatchingSids = principal.GetAllUserSids();
        var fourEyesIdentifiers = principal.GetUserIdentifiers();

        // Assert SG-04:
        // 1. Consent-matching SIDs MUST ONLY contain immutable IdP-issued tokens
        consentMatchingSids.ShouldContain(new Sid(attackerRealSub));
        consentMatchingSids.ShouldContain(new Sid(attackerRealOid));

        // 2. Consent-matching SIDs MUST NEVER contain victim's SID injected via name/preferred_username/upn
        consentMatchingSids.ShouldNotContain(new Sid(victimSid));
        consentMatchingSids.ShouldNotContain(new Sid(victimOid));
        consentMatchingSids.ShouldNotContain(new Sid("victim@company.com"));

        // 3. Four-eyes self-approval check DOES retain all identifiers so attacker cannot approve rules for their alias
        fourEyesIdentifiers.ShouldContain(victimSid);
        fourEyesIdentifiers.ShouldContain(victimOid);
        fourEyesIdentifiers.ShouldContain("victim@company.com");
    }

    [Fact]
    public void SG_09_McpEndpoints_ResolveCaller_PrioritizesRealUserSubjectOverClientId()
    {
        // Arrange
        var realUserSid = "S-1-5-21-USER-REAL";
        var realUserSub = "user-sub-abc";
        var agentClientId = "agent-client-id-xyz";

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, realUserSub),
            new("sub", realUserSub),
            new(ClaimTypes.PrimarySid, realUserSid),
            new("client_id", agentClientId),
            new("azp", agentClientId)
        };

        var identity = new ClaimsIdentity(claims, "Bearer");
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };

        // Act
        var caller = Autheris.Api.Endpoints.McpEndpoints.ResolveCaller(context, allowOpenMcp: false);

        // Assert SG-09:
        caller.PrincipalId.ShouldBe(realUserSub);
        caller.UserSid.ShouldBe(realUserSid);
        caller.PrincipalId.ShouldNotBe(agentClientId);
    }
}
