namespace Autheris.Tests.Unit.Streaming;

using System;
using System.Linq;
using System.Security.Claims;
using Autheris.Application.Events.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
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
            CreatedBySid: sid, CreatedByGroupSids: groups ?? ["G1", "G2"], CreatedByRoles: roles ?? ["Admin"], CreatedAt: createdAt ?? Now);

    [Fact]
    public void TryBuild_BuildsPrincipalFromSnapshot()
    {
        var p = CdcSubscriptionPrincipal.TryBuild(Sub(), _time, _options);

        p.ShouldNotBeNull();
        p.GetUserSid()!.Value.Value.ShouldBe("S-1-5-21-1");
        p.GetGroupSids().Select(g => g.Value).OrderBy(x => x).ShouldBe(["G1", "G2"]);
        p.GetUserRoles().ShouldBe(["Admin"], ignoreOrder: true);
    }

    [Fact]
    public void TryBuild_TenantClaimComesFromSubscription_NeverFromSnapshot()
    {
        var p = CdcSubscriptionPrincipal.TryBuild(Sub(tenant: "tenant-a", roles: ["Admin"]), _time, _options)!;

        p.FindFirst("tenant_id")!.Value.ShouldBe("tenant-a");
        p.Claims.Count(c => c.Type == "tenant_id").ShouldBe(1);
        // No other claim types than sid, groups, roles, tenant (Casbin copies every claim into attributes).
        p.Claims.Select(c => c.Type).Distinct().ShouldBe(
            [ClaimTypes.PrimarySid, ClaimTypes.GroupSid, ClaimTypes.Role, "tenant_id"], ignoreOrder: true);
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
            new Claim(ClaimTypes.GroupSid, "G-A"),
            new Claim("groups", "G-B"),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("tenant_id", "tenant-a"),
            new Claim("email", "x@y.z")
        ], "test"));

        var capture = CdcSubscriptionPrincipal.Capture(admin, _options);

        capture.IsSuccess.ShouldBeTrue();
        capture.Sid.ShouldBe("S-ADMIN");
        capture.GroupSids!.OrderBy(x => x).ShouldBe(["G-A", "G-B"]);
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
            [new Claim(ClaimTypes.PrimarySid, "S"), new Claim(ClaimTypes.GroupSid, "1"), new Claim(ClaimTypes.GroupSid, "2"), new Claim(ClaimTypes.GroupSid, "3")], "test"));

        var capture = CdcSubscriptionPrincipal.Capture(admin, options);

        capture.IsSuccess.ShouldBeFalse();
        capture.Error.ShouldBe(CdcSnapshotCaptureError.TooLarge);
    }
}
