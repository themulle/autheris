namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

[Trait("Category", "Security")]
public sealed class GroupCacheTests
{
    private const string RealmId = "workforce-entra";
    private const string SidGroupA = "S-1-5-21-100-200-300-4101";
    private const string SidGroupB = "S-1-5-21-100-200-300-4102";
    private static readonly TenantId Tenant = new("tenant-cache");

    private static IdentityRealm WorkforceRealm() => new(
        RealmId,
        IdentityRealmKind.EntraWorkforce,
        TenantBindingSpecification.Fixed(Tenant),
        RealmGroupSource.Resolver,
        RealmProvisioningMode.None,
        resolverChain: [CountingRoleProvider.Id],
        issuers: ["https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0"]);

    private static ResolvedIdentity IdentityOf(string userKey) =>
        new(userKey, $"{userKey}@corp.example", "User", $"{userKey}@corp.example", Tenant, RealmId, []);

    [Fact]
    public async Task Ttl_bound_not_exceeded()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(t0);
        var cache = new RoleResolverCache(timeProvider: time);
        var provider = new CountingRoleProvider([SidGroupA]);

        var options = Options.Create(new GatewayOptions
        {
            Authentication = new AuthenticationOptions { GroupCacheTtlMinutes = 5 }
        });
        var resolver = new RoleResolver([provider], cache, options: options, timeProvider: time);
        var realm = WorkforceRealm();
        var identity = IdentityOf("user-ttl-1");

        // Initial resolution populates L1 cache bounded by GroupCacheTtlMinutes (5 minutes).
        var first = await resolver.ResolveAsync(identity, realm, CancellationToken.None);
        first.Groups.ShouldBe([SidGroupA]);
        first.ResolvedAt.ShouldBe(t0);
        first.ExpiresAt.ShouldBe(t0.AddMinutes(5));
        provider.Calls.ShouldBe(1);

        // Just before the TTL boundary (4m59s): cache hit is valid and must not exceed ExpiresAt.
        time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        var beforeBoundary = await resolver.ResolveAsync(identity, realm, CancellationToken.None);
        beforeBoundary.Groups.ShouldBe([SidGroupA]);
        beforeBoundary.ExpiresAt.ShouldBe(t0.AddMinutes(5));
        provider.Calls.ShouldBe(1);

        // At exact expiry boundary (5m00s): cache must fail-closed and miss; resolver refreshes from provider.
        provider.SetGroups([SidGroupB]);
        time.Advance(TimeSpan.FromSeconds(1));
        (await cache.GetAsync(RealmId, "role:user-ttl-1")).ShouldBeNull();

        var refreshed = await resolver.ResolveAsync(identity, realm, CancellationToken.None);
        provider.Calls.ShouldBe(2);
        refreshed.Groups.ShouldBe([SidGroupB]);
        refreshed.ExpiresAt.ShouldBe(time.GetUtcNow().AddMinutes(5));
    }

    [Fact]
    public async Task Configured_ttl_bound_shorter_than_default_is_enforced()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(t0);
        var cache = new RoleResolverCache(timeProvider: time);
        var provider = new CountingRoleProvider([SidGroupA]);

        var options = Options.Create(new GatewayOptions
        {
            Authentication = new AuthenticationOptions { GroupCacheTtlMinutes = 2 }
        });
        var resolver = new RoleResolver([provider], cache, options: options, timeProvider: time);
        var realm = WorkforceRealm();
        var identity = IdentityOf("user-ttl-2");

        var first = await resolver.ResolveAsync(identity, realm, CancellationToken.None);
        first.ExpiresAt.ShouldBe(t0.AddMinutes(2));

        time.Advance(TimeSpan.FromMinutes(2));
        (await cache.GetAsync(RealmId, "role:user-ttl-2")).ShouldBeNull();

        provider.SetGroups([SidGroupB]);
        var second = await resolver.ResolveAsync(identity, realm, CancellationToken.None);
        second.Groups.ShouldBe([SidGroupB]);
        provider.Calls.ShouldBe(2);
    }

    private sealed class CountingRoleProvider(IReadOnlyCollection<string> initialGroups) : IRoleProvider
    {
        public const string Id = "counting-test-provider";
        private HashSet<string> _groups = new(initialGroups, StringComparer.OrdinalIgnoreCase);

        public int Calls { get; private set; }
        public string ProviderId => Id;

        public void SetGroups(IReadOnlyCollection<string> groups) =>
            _groups = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);

        public Task<ProviderRoleResult> GetRolesAsync(ResolvedIdentity identity, IdentityRealm realm, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ProviderRoleResult(
                new HashSet<string>(_groups, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        }
    }
}
