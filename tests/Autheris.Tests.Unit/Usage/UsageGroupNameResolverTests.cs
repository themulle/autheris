namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class UsageGroupNameResolverTests
{
    private const string Sid1 = "S-1-5-21-100-200-300-501";
    private const string Sid2 = "S-1-5-21-100-200-300-502";

    [Fact]
    public async Task Config_map_takes_precedence_over_scim_store()
    {
        var store = new FakeGroupStore { [( "tenant-a", Sid1 )] = "Store Group" };
        var opts = Options.Create(new UsageAccountingOptions
        {
            GroupDisplayNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Sid1] = "Configured Analysts"
            }
        });

        var resolver = new UsageGroupNameResolver(opts, store, new FakeTimeProvider());
        string name = await resolver.ResolveGroupNameAsync(new TenantId("tenant-a"), Sid1);

        name.ShouldBe("Configured Analysts");
        store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Falls_back_to_scim_store_by_object_sid_when_not_in_config()
    {
        var store = new FakeGroupStore { [( "tenant-a", Sid1 )] = "Engineering Team" };
        var resolver = new UsageGroupNameResolver(Options.Create(new UsageAccountingOptions()), store, new FakeTimeProvider());

        string resolved = await resolver.ResolveGroupNameAsync(new TenantId("tenant-a"), Sid1);
        string missing = await resolver.ResolveGroupNameAsync(new TenantId("tenant-a"), Sid2);

        resolved.ShouldBe("Engineering Team");
        missing.ShouldBe(Sid2);
        store.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Non_canonical_sid_returns_raw_without_querying_store()
    {
        var store = new FakeGroupStore { [( "tenant-a", "engineering" )] = "ShouldNotMatch" };
        var resolver = new UsageGroupNameResolver(Options.Create(new UsageAccountingOptions()), store, new FakeTimeProvider());

        string resolved = await resolver.ResolveGroupNameAsync(new TenantId("tenant-a"), "not-a-sid");

        resolved.ShouldBe("not-a-sid");
        store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Resolves_realm_bound_group_sid_from_scim_store()
    {
        const string realmGroupSid = "realm:oidc-a:eng";
        var store = new FakeGroupStore { [("tenant-a", realmGroupSid)] = "Partner Engineering" };
        var resolver = new UsageGroupNameResolver(Options.Create(new UsageAccountingOptions()), store, new FakeTimeProvider());

        string resolved = await resolver.ResolveGroupNameAsync(new TenantId("tenant-a"), realmGroupSid);

        resolved.ShouldBe("Partner Engineering");
        store.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Caches_per_tenant_and_sid_with_ten_minute_ttl()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var store = new FakeGroupStore { [( "tenant-a", Sid1 )] = "Finance V1" };
        var resolver = new UsageGroupNameResolver(Options.Create(new UsageAccountingOptions()), store, time);

        (await resolver.ResolveGroupNameAsync(new TenantId("tenant-a"), Sid1)).ShouldBe("Finance V1");
        store[("tenant-a", Sid1)] = "Finance V2";

        time.Advance(TimeSpan.FromMinutes(9));
        (await resolver.ResolveGroupNameAsync(new TenantId("tenant-a"), Sid1)).ShouldBe("Finance V1");
        store.Calls.ShouldBe(1);

        time.Advance(TimeSpan.FromMinutes(2));
        (await resolver.ResolveGroupNameAsync(new TenantId("tenant-a"), Sid1)).ShouldBe("Finance V2");
        store.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Cache_is_isolated_by_tenant()
    {
        var store = new FakeGroupStore
        {
            [("tenant-a", Sid1)] = "Alpha Analysts",
            [("tenant-b", Sid1)] = "Beta Analysts"
        };
        var resolver = new UsageGroupNameResolver(Options.Create(new UsageAccountingOptions()), store, new FakeTimeProvider());

        (await resolver.ResolveGroupNameAsync(new TenantId("tenant-a"), Sid1)).ShouldBe("Alpha Analysts");
        (await resolver.ResolveGroupNameAsync(new TenantId("tenant-b"), Sid1)).ShouldBe("Beta Analysts");
        store.Calls.ShouldBe(2);
    }

    [Fact]
    public void Resolver_has_no_ldap_dependency()
    {
        var ctorParams = typeof(UsageGroupNameResolver).GetConstructors()[0].GetParameters();
        ctorParams.Any(p => p.ParameterType.Name.Contains("Ldap", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    private sealed class FakeGroupStore : Dictionary<(string Tenant, string Sid), string>, IScimGroupStore
    {
        public int Calls { get; private set; }

        public Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) =>
            Task.FromResult<ScimGroup?>(null);

        public Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) =>
            Task.FromResult<ScimGroup?>(null);

        public Task<ScimGroup?> GetGroupByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) =>
            Task.FromResult<ScimGroup?>(null);

        public Task<string?> GetGroupDisplayNameBySidAsync(TenantId tenant, Sid groupSid, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(TryGetValue((tenant.Value, groupSid.Value), out var val) ? val : null);
        }

        public Task CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<EffectiveScimGroupsResult> GetEffectiveGroupSidsForUserAsync(TenantId tenantId, string realmId, string userKey, int maxDepth = 32, CancellationToken ct = default) =>
            Task.FromResult(new EffectiveScimGroupsResult(new HashSet<string>(), false));
        public Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScimGroup>>([]);
        public Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetChildGroupIdsAsync(TenantId tenantId, string realmId, string parentGroupId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<ScimGroupNestingEdge>> ListGroupNestingEdgesAsync(TenantId tenantId, string realmId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScimGroupNestingEdge>>([]);
    }
}
