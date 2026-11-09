namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class IdentityPipelineTests
{
    private const string UserSid = "S-1-5-21-1-2-3-1001";
    private const string GroupSid = "S-1-5-21-1-2-3-1100";

    private static IIdentityRealmRegistry Registry(RealmGroupSource source = RealmGroupSource.None)
    {
        var registry = Substitute.For<IIdentityRealmRegistry>();
        var realm = new IdentityRealm(
            "test-realm",
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, FixedTenantId: new TenantId("tenant-a")),
            groupSource: source);
        registry.GetDefaultRealm().Returns(realm);
        registry.GetRealm("test-realm").Returns(realm);
        return registry;
    }

    private static ClaimsPrincipal Raw(string authenticationType, params Claim[] claims) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, UserSid), new Claim("sub", "user-1"), .. claims], authenticationType));

    [Theory]
    [InlineData("Bearer")]
    [InlineData("Basic")]
    [InlineData("ForwardAuth")]
    [InlineData("Negotiate")]
    [InlineData("WebSocket")]
    public async Task Privileged_subject_rejected_for_every_scheme(string scheme)
    {
        var pipeline = TestIdentityPipeline.Create(Registry());
        var raw = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-32-544")], scheme));

        var result = await pipeline.ProcessAsync(raw, scheme, null, CancellationToken.None);

        result.Identity!.IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task Privileged_sub_claim_rejected_without_sid_claim()
    {
        var pipeline = TestIdentityPipeline.Create(Registry());
        var raw = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "S-1-5-32-544")], "Bearer"));

        var result = await pipeline.ProcessAsync(raw, "Bearer", null, CancellationToken.None);

        result.Identity!.IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task Normalization_runs_once_per_request()
    {
        var groupResolver = Substitute.For<IGroupSidResolver>();
        groupResolver.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IdentityRealm>(), Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GroupResolution(new HashSet<Sid> { new(GroupSid) }, GroupResolutionStatus.Complete, "token", DateTimeOffset.UtcNow.AddMinutes(5)));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var transformation = new EnterpriseClaimsTransformation(TestIdentityPipeline.Create(Registry(RealmGroupSource.Token), groupResolver: groupResolver), accessor);
        var raw = Raw("Bearer", new Claim("iss", "https://issuer.example"));

        var first = await transformation.TransformAsync(raw);
        var second = await transformation.TransformAsync(raw);
        var third = await transformation.TransformAsync(first);

        await groupResolver.Received(1).ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IdentityRealm>(), Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        second.ShouldBeSameAs(first);
        third.ShouldBeSameAs(first);
        first.GetGroupSids().ShouldBe([new Sid(GroupSid)]);
    }

    [Fact]
    public async Task Degraded_groups_deny_data_access()
    {
        var pipeline = TestIdentityPipeline.Create(Registry(RealmGroupSource.Token));
        var raw = Raw("Bearer", new Claim("iss", "https://issuer.example"), new Claim("groups", "not-a-sid"));

        var result = await pipeline.ProcessAsync(raw, "Bearer", null, CancellationToken.None);

        result.Identity!.IsAuthenticated.ShouldBeTrue();
        GroupResolutionClaims.Read(result).ShouldBe(GroupResolutionStatus.Degraded);
        result.GetGroupSids().ShouldBeEmpty();
        GroupResolutionClaims.IsMarkedDegraded(result).ShouldBeTrue();
        result.HasClaim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded).ShouldBeTrue();
    }

    [Fact]
    public async Task Pipeline_strips_inbound_group_status_and_marker_claims()
    {
        var pipeline = TestIdentityPipeline.Create(Registry());
        var raw = Raw(
            "Bearer",
            new Claim("groups", GroupSid),
            new Claim("groupsid", GroupSid),
            new Claim("http://schemas.microsoft.com/ws/2008/06/identity/claims/groups", GroupSid),
            new Claim("autheris:group", "realm|admins"),
            new Claim("autheris:realm", "forged-realm"),
            new Claim(GroupResolutionClaims.StatusClaimType, GroupResolutionClaims.CompleteValue),
            new Claim(GroupResolutionClaims.ExpiresAtClaimType, "4102444800"),
            new Claim(ClaimTypes.GroupSid, GroupSid));

        var result = await pipeline.ProcessAsync(raw, "Bearer", null, CancellationToken.None);

        result.Claims.Where(c => c.Type is "groups" or "groupsid" or "autheris:group").ShouldBeEmpty();
        result.Claims.Where(c => c.Type == "autheris:realm").Select(c => c.Value).ShouldBe(["test-realm"]);
        result.GetGroupSids().ShouldBeEmpty();
        result.Claims.Where(c => c.Type == GroupResolutionClaims.StatusClaimType).ShouldAllBe(c => c.Issuer == IdentityClaimIssuers.Pipeline);
        result.Claims.Count(c => c.Type == GroupResolutionClaims.StatusClaimType).ShouldBe(1);
        GroupResolutionClaims.ReadExpiresAt(result)!.Value.Year.ShouldBeLessThan(2100);
    }

    [Fact]
    public async Task Pipeline_failure_returns_unauthenticated_principal()
    {
        var groupResolver = Substitute.For<IGroupSidResolver>();
        groupResolver.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IdentityRealm>(), Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<GroupResolution>>(_ => throw new InvalidOperationException("resolver crashed"));
        var pipeline = TestIdentityPipeline.Create(Registry(RealmGroupSource.Token), groupResolver: groupResolver);

        var result = await pipeline.ProcessAsync(Raw("Bearer", new Claim("iss", "https://issuer.example")), "Bearer", null, CancellationToken.None);

        result.Identity!.IsAuthenticated.ShouldBeFalse();
        result.Claims.ShouldBeEmpty();
    }

    [Fact]
    public async Task Marker_with_foreign_issuer_is_reprocessed()
    {
        var pipeline = TestIdentityPipeline.Create(Registry(RealmGroupSource.Token));
        var raw = Raw(
            "Bearer",
            new Claim("iss", "https://issuer.example"),
            new Claim("groups", "not-a-sid"),
            new Claim(GroupResolutionClaims.MarkerClaimType, "1", ClaimValueTypes.String, "https://issuer.example"),
            new Claim(GroupResolutionClaims.StatusClaimType, GroupResolutionClaims.CompleteValue, ClaimValueTypes.String, "https://issuer.example"));

        var result = await pipeline.ProcessAsync(raw, "Bearer", null, CancellationToken.None);

        GroupResolutionClaims.HasPipelineMarker(result).ShouldBeTrue();
        GroupResolutionClaims.Read(result).ShouldBe(GroupResolutionStatus.Degraded);
        result.Claims.Count(c => c.Type == GroupResolutionClaims.MarkerClaimType).ShouldBe(1);
    }

    [Fact]
    public void Principal_without_pipeline_marker_is_degraded()
    {
        var principal = Raw("Bearer", IdentityClaimIssuers.CreateGroupSidClaim(GroupSid), new Claim("tenant_id", "tenant-a"));
        var context = new DefaultHttpContext { User = principal };

        var securityContext = SecurityContextFactory.CreateFromHttpContext(context);

        GroupResolutionClaims.Read(principal).ShouldBe(GroupResolutionStatus.Degraded);
        securityContext.GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Degraded);
        securityContext.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task Multiple_identities_are_collapsed_to_one_pipeline_identity()
    {
        var pipeline = TestIdentityPipeline.Create(Registry());
        var raw = Raw("Bearer", new Claim("tenant_id", "tenant-a"));
        raw.AddIdentity(new ClaimsIdentity([new Claim("groups", GroupSid), new Claim(ClaimTypes.GroupSid, GroupSid, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline)], "Other"));

        var result = await pipeline.ProcessAsync(raw, "Bearer", null, CancellationToken.None);

        result.Identities.Count().ShouldBe(1);
        result.GetGroupSids().ShouldBeEmpty();
        result.Claims.Where(c => c.Type == GroupResolutionClaims.MarkerClaimType).ShouldAllBe(c => c.Issuer == IdentityClaimIssuers.Pipeline);
    }

    [Fact]
    public async Task Pipeline_emits_resolved_groups_with_pipeline_issuer_and_complete_status()
    {
        var groupResolver = Substitute.For<IGroupSidResolver>();
        groupResolver.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IdentityRealm>(), Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GroupResolution(new HashSet<Sid> { new(GroupSid) }, GroupResolutionStatus.Complete, "token", DateTimeOffset.UtcNow.AddMinutes(5)));
        var pipeline = TestIdentityPipeline.Create(Registry(RealmGroupSource.Token), groupResolver: groupResolver);

        var result = await pipeline.ProcessAsync(Raw("Bearer", new Claim("iss", "https://issuer.example")), "Bearer", null, CancellationToken.None);

        GroupResolutionClaims.Read(result).ShouldBe(GroupResolutionStatus.Complete);
        result.GetGroupSids().ShouldBe([new Sid(GroupSid)]);
        GroupResolutionClaims.ReadExpiresAt(result).ShouldNotBeNull();
    }

    [Fact]
    public async Task Pipeline_output_carries_no_authorization_derived_claim()
    {
        // The upstream-identity principal never holds the inbound credential; the pipeline adds none either.
        var pipeline = TestIdentityPipeline.Create(Registry());

        var result = await pipeline.ProcessAsync(Raw("ForwardAuth"), "ForwardAuth", null, CancellationToken.None);

        result.Claims.Where(c => c.Type is "access_token" or "id_token" or "authorization" or "token").ShouldBeEmpty();
    }
}
