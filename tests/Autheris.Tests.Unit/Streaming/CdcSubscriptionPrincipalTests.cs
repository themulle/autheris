namespace Autheris.Tests.Unit.Streaming;

using System;
using System.Linq;
using System.Security.Claims;
using Autheris.Application.Events.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class CdcSubscriptionPrincipalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);
    private readonly CdcWebhooksOptions _options = new();

    private static CloudEventWebhookSubscription Sub(
        string? sid = "S-1-5-21-1", string[]? groups = null, string[]? roles = null, DateTimeOffset? createdAt = null, string tenant = "tenant-a") =>
        new("sub-1", tenant, "https://example.test/h", "*", [], "secret",
            CreatedBySid: sid, CreatedByGroupSids: groups ?? ["S-1-5-21-1-2-3-3101", "S-1-5-21-1-2-3-3102"], CreatedByRoles: roles ?? ["Admin"], CreatedAt: createdAt ?? Now);

    [Fact]
    public void TryBuild_BuildsPrincipalFromSnapshot()
    {
        var p = CdcSubscriptionPrincipal.TryBuild(Sub(), _time, _options);

        p.ShouldNotBeNull();
        p.GetUserSid()!.Value.Value.ShouldBe("S-1-5-21-1");
        p.GetGroupSids().Select(g => g.Value).OrderBy(x => x).ShouldBe(["S-1-5-21-1-2-3-3101", "S-1-5-21-1-2-3-3102"]);
        p.GetUserRoles().ShouldBe(["Admin"], ignoreOrder: true);
    }

    [Fact]
    public void TryBuild_TenantClaimComesFromSubscription_NeverFromSnapshot()
    {
        var p = CdcSubscriptionPrincipal.TryBuild(Sub(tenant: "tenant-a", roles: ["Admin"]), _time, _options)!;

        p.FindFirst("tenant_id")!.Value.ShouldBe("tenant-a");
        p.Claims.Count(c => c.Type == "tenant_id").ShouldBe(1);
        // No other claim types than sid, groups, roles, tenant and the pipeline marker and status (Casbin copies every claim into attributes).
        p.Claims.Select(c => c.Type).Distinct().ShouldBe(
            [ClaimTypes.PrimarySid, ClaimTypes.GroupSid, ClaimTypes.Role, "tenant_id",
             Autheris.Domain.Identity.GroupResolutionClaims.MarkerClaimType, Autheris.Domain.Identity.GroupResolutionClaims.StatusClaimType, Autheris.Domain.Identity.GroupResolutionClaims.ExpiresAtClaimType], ignoreOrder: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryBuild_MissingSid_Denies(string? sid) =>
        CdcSubscriptionPrincipal.TryBuild(Sub(sid: sid), _time, _options).ShouldBeNull();

    [Fact]
    public void TryBuild_MissingSnapshotMembers_Denies()
    {
        var s = Sub() with { CreatedByGroupSids = null };
        CdcSubscriptionPrincipal.TryBuild(s, _time, _options).ShouldBeNull();
        CdcSubscriptionPrincipal.TryBuild(Sub() with { CreatedByRoles = null }, _time, _options).ShouldBeNull();
        CdcSubscriptionPrincipal.TryBuild(Sub() with { CreatedAt = null }, _time, _options).ShouldBeNull();
    }

    [Fact]
    public void TryBuild_ExpiredSnapshot_Denies()
    {
        var s = Sub(createdAt: Now.AddDays(-91));
        CdcSubscriptionPrincipal.TryBuild(s, _time, _options).ShouldBeNull();
    }

    [Fact]
    public void TryBuild_SnapshotAtLimit_IsStillValid_AndZeroMeansUnlimited()
    {
        CdcSubscriptionPrincipal.TryBuild(Sub(createdAt: Now.AddDays(-89)), _time, _options).ShouldNotBeNull();
        var unlimited = new CdcWebhooksOptions { CreatorSnapshotMaxAgeDays = 0 };
        CdcSubscriptionPrincipal.TryBuild(Sub(createdAt: Now.AddDays(-4000)), _time, unlimited).ShouldNotBeNull();
    }

    [Fact]
    public void Capture_RoundTripsSidGroupsAndRoles()
    {
        var admin = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-ADMIN"),
            Autheris.Domain.Common.IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-21-1-2-3-3103"),
            new Claim("groups", "S-1-5-21-1-2-3-3104"),
            new Claim(ClaimTypes.GroupSid, "S-1-5-21-1-2-3-3105"),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("tenant_id", "tenant-a"),
            new Claim("email", "x@y.z"),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker(), Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(Autheris.Domain.Identity.GroupResolutionStatus.Complete)
        ], "test"));

        var capture = CdcSubscriptionPrincipal.Capture(admin, _options);

        capture.IsSuccess.ShouldBeTrue();
        capture.Sid.ShouldBe("S-ADMIN");
        // Only the pipeline-stamped group counts; token claim types and foreign-issuer group claims are not captured.
        capture.GroupSids!.ShouldBe(["S-1-5-21-1-2-3-3103"]);
        capture.Roles.ShouldBe(["Admin"]);
    }

    [Fact]
    public void Capture_WithoutSid_Fails()
    {
        var anon = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "test"));
        CdcSubscriptionPrincipal.Capture(anon, _options).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Capture_TooManyGroups_IsRejectedNotTruncated()
    {
        var options = new CdcWebhooksOptions { MaxSnapshotGroups = 2 };
        var admin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S"), Autheris.Domain.Common.IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-21-1-2-3-1"), Autheris.Domain.Common.IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-21-1-2-3-2"), Autheris.Domain.Common.IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-21-1-2-3-3"), Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker(), Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(Autheris.Domain.Identity.GroupResolutionStatus.Complete)], "test"));

        var capture = CdcSubscriptionPrincipal.Capture(admin, options);

        capture.IsSuccess.ShouldBeFalse();
        capture.Error.ShouldBe(CdcSnapshotCaptureError.TooLarge);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(false)]
    [InlineData(true)]
    public void Capture_WithUnresolvedGroups_IsRejected(bool markedDegraded)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-ADMIN"), new(ClaimTypes.Role, "Admin") };
        if (markedDegraded)
        {
            claims.Add(Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker());
            claims.Add(Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(Autheris.Domain.Identity.GroupResolutionStatus.Degraded));
        }

        var capture = CdcSubscriptionPrincipal.Capture(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), _options);

        capture.IsSuccess.ShouldBeFalse();
        capture.Error.ShouldBe(CdcSnapshotCaptureError.GroupsUnresolved);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TryBuild_WithoutAgeLimit_StillBoundsTheGroupValidity()
    {
        var options = new CdcWebhooksOptions { CreatorSnapshotMaxAgeDays = 0 };

        var p = CdcSubscriptionPrincipal.TryBuild(Sub(), _time, options)!;

        var expiry = Autheris.Domain.Identity.GroupResolutionClaims.ReadExpiresAt(p);
        expiry.ShouldNotBeNull();
        expiry.Value.ShouldBeGreaterThan(Now);
        expiry.Value.ShouldBeLessThanOrEqualTo(Now.AddHours(1));
    }

    [Fact]
    public void Capture_WithRestrictedScope_FailsWithRestrictedScopeError()
    {
        var restrictedPolicy = RestrictedScopePolicy.FromOptions(new RestrictedScopeOptions { Enabled = true, Scopes = ["restricted-analytics"] });
        var admin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S"), new Claim(ClaimTypes.Role, "Admin"), new Claim("scp", "restricted-analytics")], "test"));

        var capture = CdcSubscriptionPrincipal.Capture(admin, _options, restrictedPolicy);

        capture.IsSuccess.ShouldBeFalse();
        capture.Error.ShouldBe(CdcSnapshotCaptureError.RestrictedScope);
    }

    [Fact]
    public void Evaluate_DistinguishesExpiredFromMissingSnapshot()
    {
        CdcSubscriptionPrincipal.Evaluate(Sub(createdAt: Now.AddDays(-91)), _time, _options, out var p1);
        CdcSubscriptionPrincipal.Evaluate(Sub(sid: null), _time, _options, out var p2);
        CdcSubscriptionPrincipal.Evaluate(Sub(), _time, _options, out var p3);

        p1.ShouldBeNull();
        p2.ShouldBeNull();
        p3.ShouldNotBeNull();
        CdcSubscriptionPrincipal.Evaluate(Sub(createdAt: Now.AddDays(-91)), _time, _options, out _).ShouldBe(CdcSnapshotDenial.Expired);
        CdcSubscriptionPrincipal.Evaluate(Sub(sid: null), _time, _options, out _).ShouldBe(CdcSnapshotDenial.Missing);
        CdcSubscriptionPrincipal.Evaluate(Sub(), _time, _options, out _).ShouldBe(CdcSnapshotDenial.None);
    }
}
