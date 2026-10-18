namespace Autheris.Tests.Unit.Identity.Attributes;

using System.Linq;
using System.Security.Claims;
using Autheris.Application.Identity.Attributes;
using Shouldly;
using Xunit;
using static CallerAttributeTestSupport;

/// <summary>ST-129-01 (BC-129-01): subject classification matrix. <c>GetUserSid()</c> falls back to application ids; this factory must not.</summary>
[Trait("Category", "Security")]
public sealed class CallerSubjectFactoryTests
{
    private static CallerSubject Create(ClaimsPrincipal? principal, params string[] personIssuers) =>
        CallerSubjectFactory.Create(principal, Tenant, Options(true, personIssuers));

    [Fact]
    public void ST_129_01_EntraUser_IsUser_WithTheObjectIdAsKey()
    {
        var subject = Create(EntraUser());
        subject.Kind.ShouldBe(CallerSubjectKind.User);
        subject.KeyFor(SubjectKeyKind.EntraObjectId).ShouldBe(UserOid);
        subject.ApplicationId.ShouldBeNull();
    }

    [Fact]
    public void ST_129_01_EntraUser_WithoutHumanClaims_ButWithAzp_IsStillUser()
    {
        // The person token that carries azp (client app id) and an oid: the claim-presence classification of the identity resolver says "app".
        var subject = Create(Principal(Bearer, [("oid", UserOid), ("azp", "99999999-9999-9999-9999-999999999999")]));
        subject.Kind.ShouldBe(CallerSubjectKind.User);
        subject.KeyFor(SubjectKeyKind.EntraObjectId).ShouldBe(UserOid);
    }

    [Fact]
    public void ST_129_01_AppOnlyToken_IsApplication_NeverUser_AndHasNoUserKey()
    {
        var subject = Create(Principal(Bearer, [("idtyp", "app"), ("oid", UserOid), ("appid", "aaaaaaaa-0000-0000-0000-000000000001")]));
        subject.Kind.ShouldBe(CallerSubjectKind.Application);
        subject.ApplicationId.ShouldBe("aaaaaaaa-0000-0000-0000-000000000001");
        subject.KeyFor(SubjectKeyKind.EntraObjectId).ShouldBeNull();
    }

    [Fact]
    public void ST_129_01_AppToken_WithAStrayEmailClaim_StaysApplication_WhenIdtypIsApp()
    {
        var subject = Create(Principal(Bearer, [("idtyp", "app"), ("oid", UserOid), ("email", "x@example.com"), ("appid", "aaaaaaaa-0000-0000-0000-000000000001")]));
        subject.Kind.ShouldBe(CallerSubjectKind.Application);
        subject.KeyFor(SubjectKeyKind.EntraObjectId).ShouldBeNull();
    }

    [Fact]
    public void ST_129_01_ClientCredentialsToken_SubEqualsClientId_IsApplication()
    {
        var subject = Create(Principal(Bearer, [("sub", "client-1"), ("client_id", "client-1")]), TrustedIssuer);
        subject.Kind.ShouldBe(CallerSubjectKind.Application);
        subject.ApplicationId.ShouldBe("client-1");
        subject.KeyFor(SubjectKeyKind.Subject).ShouldBeNull();
    }

    [Fact]
    public void ST_129_01_ServicePrincipalShape_OidEqualsSub_WithoutHumanClaim_IsApplication()
    {
        var subject = Create(Principal(Bearer, [("oid", UserOid), ("sub", UserOid), ("appid", "aaaaaaaa-0000-0000-0000-000000000001")]));
        subject.Kind.ShouldBe(CallerSubjectKind.Application);
    }

    [Fact]
    public void ST_129_01_SubKey_IsOnlyUsed_ForAnIssuerFlaggedAsPersonIssuer()
    {
        var claims = new[] { ("sub", "person-77") };
        Create(Principal(Bearer, claims)).Kind.ShouldBe(CallerSubjectKind.None);
        var subject = Create(Principal(Bearer, claims), TrustedIssuer);
        subject.Kind.ShouldBe(CallerSubjectKind.User);
        subject.KeyFor(SubjectKeyKind.Subject).ShouldBe("person-77");
    }

    [Fact]
    public void ST_129_01_OnPremSid_IsAWindowsSidKey()
    {
        var subject = Create(Principal(Bearer, [("onprem_sid", "S-1-5-21-1-2-3-1001")]));
        subject.Kind.ShouldBe(CallerSubjectKind.User);
        subject.KeyFor(SubjectKeyKind.WindowsSid).ShouldBe("S-1-5-21-1-2-3-1001");
    }

    [Theory]
    [InlineData("ForwardAuth")]
    [InlineData("Basic")]
    [InlineData("TestAuth")]
    [InlineData("Certificate")]
    [InlineData(null)]
    public void ST_129_01_NonBearerSchemes_AreNone_ByDefault(string? scheme)
    {
        var subject = Create(Principal(scheme, [("oid", UserOid), ("upn", "alice@example.com")]));
        subject.Kind.ShouldBe(CallerSubjectKind.None);
        subject.KeyFor(SubjectKeyKind.EntraObjectId).ShouldBeNull();
    }

    [Fact]
    public void ST_129_01_AnonymousAndNull_AreNone()
    {
        Create(null).Kind.ShouldBe(CallerSubjectKind.None);
        Create(new ClaimsPrincipal(new ClaimsIdentity())).Kind.ShouldBe(CallerSubjectKind.None);
    }

    [Fact]
    public void ST_129_01_UpnAndEmail_NeverBecomeAKey()
    {
        var subject = Create(Principal(Bearer, [("upn", "alice@example.com"), ("email", "alice@example.com"), ("preferred_username", "alice")]), TrustedIssuer);
        subject.Kind.ShouldBe(CallerSubjectKind.None);
        subject.KeyFor(SubjectKeyKind.EntraObjectId).ShouldBeNull();
        subject.KeyFor(SubjectKeyKind.WindowsSid).ShouldBeNull();
        subject.KeyFor(SubjectKeyKind.Subject).ShouldBeNull();
    }

    [Fact]
    public void ST_129_01_ClaimsOfANonBearerIdentity_AreNotPartOfTheSubject()
    {
        var principal = EntraUser(extra: ("department", "legit"));
        var header = new ClaimsIdentity("ForwardAuth");
        header.AddClaim(new Claim("department", "forged", ClaimValueTypes.String, "LOCAL AUTHORITY"));
        principal.AddIdentity(header);

        var subject = Create(principal);
        subject.Claims.Where(c => c.Type == "department").Select(c => c.Value).ShouldBe(["legit"]);
    }

    [Fact]
    public void ST_129_01_ConflictingApplicationIdCandidates_LeaveTheApplicationIdEmpty()
    {
        var subject = Create(Principal(Bearer, [("idtyp", "app"), ("appid", "aaaaaaaa-0000-0000-0000-000000000001"), ("azp", "bbbbbbbb-0000-0000-0000-000000000002")]));
        subject.Kind.ShouldBe(CallerSubjectKind.Application);
        subject.ApplicationId.ShouldBeNull();
    }

    [Fact]
    public void ST_129_01_ApplicationId_IsGuidCanonicalised()
    {
        var subject = Create(Principal(Bearer, [("idtyp", "app"), ("appid", "AAAAAAAA-0000-0000-0000-000000000001")]));
        subject.ApplicationId.ShouldBe("aaaaaaaa-0000-0000-0000-000000000001");
    }

    [Fact]
    public void ST_129_22_ToString_PrintsNoIdentityData()
    {
        var text = Create(EntraUser()).ToString();
        text.ShouldNotContain(UserOid);
        text.ShouldNotContain("alice");
    }

    [Fact]
    public void TheSubject_IsAnImmutableSnapshot_OfTheClaimsAtCreation()
    {
        var principal = EntraUser(extra: ("department", "a"));
        var subject = Create(principal);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("department", "b"));
        subject.Claims.Count(c => c.Type == "department").ShouldBe(1);
    }
}
