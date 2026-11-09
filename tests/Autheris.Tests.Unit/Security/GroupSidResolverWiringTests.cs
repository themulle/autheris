namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>The group sources as wired behind the identity pipeline: one realm, one tenant, the scheme selects the source.</summary>
[Trait("Category", "Security")]
public sealed class GroupSidResolverWiringTests
{
    private const string UserSid = "S-1-5-21-1-2-3-1001";
    private const string GroupA = "S-1-5-21-1-2-3-1100";
    private const string GroupB = "S-1-5-21-1-2-3-1101";

    private static IIdentityRealmRegistry Registry(RealmGroupSource source, IdentityRealmKind kind = IdentityRealmKind.EntraWorkforce, params string[] chain)
    {
        var registry = Substitute.For<IIdentityRealmRegistry>();
        var realm = new IdentityRealm(
            "wiring-realm",
            kind,
            new TenantBindingSpecification(TenantBindingKind.Fixed, FixedTenantId: new TenantId("tenant-a")),
            groupSource: source,
            resolverChain: chain.Length == 0 ? null : chain);
        registry.GetDefaultRealm().Returns(realm);
        registry.GetRealm("wiring-realm").Returns(realm);
        registry.ResolveRealmForScheme(Arg.Any<string?>(), Arg.Any<string?>()).Returns(realm);
        return registry;
    }

    private static ClaimsPrincipal Principal(string authenticationType, params Claim[] claims) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, UserSid), new Claim("sub", "user-1"), .. claims], authenticationType));

    [Fact]
    public async Task Negotiate_pac_groups_are_taken()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions());
        var resolver = new GroupSidResolver(null, [], options, isWindowsHost: true);
        var pipeline = TestIdentityPipeline.Create(Registry(RealmGroupSource.Token, IdentityRealmKind.Kerberos), groupResolver: resolver);

        var result = await pipeline.ProcessAsync(Principal("Negotiate", new Claim(ClaimTypes.GroupSid, GroupA), new Claim(ClaimTypes.GroupSid, GroupB)), "Negotiate", null, CancellationToken.None);

        result.GetGroupSids().Select(g => g.Value).ShouldBe([GroupA, GroupB], ignoreOrder: true);
        GroupResolutionClaims.Read(result).ShouldBe(GroupResolutionStatus.Complete);
    }

    [Fact]
    public async Task Basic_configured_group_sids_are_taken()
    {
        var pipeline = TestIdentityPipeline.Create(Registry(RealmGroupSource.Token, IdentityRealmKind.Basic));

        var result = await pipeline.ProcessAsync(Principal("Basic", IdentityClaimIssuers.CreateGroupSidClaim(GroupA)), "Basic", null, CancellationToken.None);

        result.GetGroupSids().Select(g => g.Value).ShouldBe([GroupA]);
    }

    [Fact]
    public async Task Basic_resolver_realm_uses_chain()
    {
        var provider = Substitute.For<IRoleProvider>();
        provider.ProviderId.Returns("static-mapping");
        provider.GetRolesAsync(Arg.Any<ResolvedIdentity>(), Arg.Any<IdentityRealm>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProviderRoleResult(new HashSet<string> { GroupB }, new HashSet<string>(), new Dictionary<string, string>())));
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions());
        var roleResolver = new RoleResolver([provider], new RoleResolverCache(), null, options);
        var pipeline = TestIdentityPipeline.Create(
            Registry(RealmGroupSource.Resolver, IdentityRealmKind.Basic, "static-mapping"),
            roleResolver: roleResolver,
            groupResolver: new GroupSidResolver(roleResolver, [provider], options));

        var result = await pipeline.ProcessAsync(Principal("Basic"), "Basic", null, CancellationToken.None);

        result.GetGroupSids().Select(g => g.Value).ShouldBe([GroupB]);
        GroupResolutionClaims.Read(result).ShouldBe(GroupResolutionStatus.Complete);
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("Basic")]
    [InlineData("ForwardAuth")]
    public async Task Same_user_same_groups_identical_security_context_across_schemes(string scheme)
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions { GroupSource = ForwardAuthGroupSource.Header }
            }
        };
        var pipeline = TestIdentityPipeline.Create(Registry(RealmGroupSource.Token), options: options);
        var raw = scheme == "Bearer"
            ? Principal("AuthenticationTypes.Federation", new Claim("iss", "https://issuer.example"), new Claim("groups", GroupA), new Claim("groups", GroupB))
            : Principal(scheme, IdentityClaimIssuers.CreateGroupSidClaim(GroupA), IdentityClaimIssuers.CreateGroupSidClaim(GroupB));

        var processed = await pipeline.ProcessAsync(raw, scheme, null, CancellationToken.None);
        var context = SecurityContextFactory.CreateFromHttpContext(new DefaultHttpContext { User = processed });

        context.UserSid.Value.ShouldBe(UserSid);
        context.GroupSids.Select(g => g.Value).ShouldBe([GroupA, GroupB], ignoreOrder: true);
        context.GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Complete);
        context.TenantId.Value.ShouldBe("tenant-a");
    }
}
