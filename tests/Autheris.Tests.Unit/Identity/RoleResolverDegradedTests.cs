namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Providers;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>GOV-08: a role provider outage is a degraded resolution that is never cached, not an empty group set.</summary>
[Trait("Category", "Security")]
public sealed class RoleResolverDegradedTests
{
    private static readonly IdentityRealm Realm = new(
        "customer-b2b",
        IdentityRealmKind.Customer,
        new TenantBindingSpecification(TenantBindingKind.Fixed, "tenant-42"),
        RealmGroupSource.Resolver,
        RealmProvisioningMode.Scim,
        issuers: ["https://customer.auth.com"],
        resolverChain: ["p1", "p2"]);

    private static readonly ResolvedIdentity Identity = new(
        "user-1", "u@c.com", "User", "u@c.com", new TenantId("tenant-42"), "customer-b2b", new List<Claim>());

    private readonly IRoleResolverCache _cache = Substitute.For<IRoleResolverCache>();

    private static IRoleProvider Provider(string id, Func<ProviderRoleResult> result)
    {
        var provider = Substitute.For<IRoleProvider>();
        provider.ProviderId.Returns(id);
        provider.GetRolesAsync(Arg.Any<ResolvedIdentity>(), Arg.Any<IdentityRealm>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(result()));
        return provider;
    }

    private static ProviderRoleResult Groups(params string[] groups) =>
        new(new HashSet<string>(groups), new HashSet<string>(), new Dictionary<string, string>());

    [Fact]
    public async Task ProviderThrows_ResolutionDegraded_NotCached()
    {
        var sut = new RoleResolver([Provider("p1", () => throw new InvalidOperationException("down")), Provider("p2", () => Groups("g"))], _cache);

        var resolution = await sut.ResolveAsync(Identity, Realm);

        resolution.IsDegraded.ShouldBeTrue();
        await _cache.DidNotReceiveWithAnyArgs().SetAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task GraphThrottled_ReturnsFailedResult()
    {
        var adapter = Substitute.For<IGraphClientAdapter>();
        adapter.GetTransitiveMemberOfAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<string>>>(_ => throw new GraphThrottledException(TimeSpan.FromSeconds(5), "429"));
        var provider = new GraphRoleProvider(adapter);

        var result = await provider.GetRolesAsync(Identity, Realm);

        result.Failed.ShouldBeTrue();
        result.Groups.ShouldBeEmpty();
    }

    [Fact]
    public async Task GraphCircuitOpen_ReturnsFailedResult()
    {
        var adapter = Substitute.For<IGraphClientAdapter>();
        adapter.GetTransitiveMemberOfAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<string>>>(_ => throw new InvalidOperationException("boom"));
        var provider = new GraphRoleProvider(adapter, failureThreshold: 1, circuitBreakDuration: TimeSpan.FromMinutes(1));

        await provider.GetRolesAsync(Identity, Realm);
        var whileOpen = await provider.GetRolesAsync(Identity, Realm);

        provider.IsCircuitBreakerOpen.ShouldBeTrue();
        whileOpen.Failed.ShouldBeTrue();
    }

    [Fact]
    public async Task MixedProviders_OneFails_Degraded()
    {
        var sut = new RoleResolver([Provider("p1", () => ProviderRoleResult.FailedResult), Provider("p2", () => Groups("g"))], _cache);

        var resolution = await sut.ResolveAsync(Identity, Realm);

        resolution.Status.ShouldBe(RoleResolutionStatus.Degraded);
        resolution.Groups.ShouldContain("customer-b2b|g");
        await _cache.DidNotReceiveWithAnyArgs().SetAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task NoGroups_Success_ResolvedAndCached()
    {
        var sut = new RoleResolver([Provider("p1", () => ProviderRoleResult.Empty), Provider("p2", () => ProviderRoleResult.Empty)], _cache);

        var resolution = await sut.ResolveAsync(Identity, Realm);

        resolution.IsDegraded.ShouldBeFalse();
        resolution.Groups.ShouldBeEmpty();
        await _cache.Received(1).SetAsync("customer-b2b", "user-1", resolution, Arg.Any<CancellationToken>());
    }
}
