namespace Autheris.Tests.Unit.Security;

using System.Security.Claims;
using System.Threading.Channels;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Providers;
using Autheris.Application.Identity.Services;
using Autheris.Application.Products;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Governance;
using Autheris.Tests.Unit.Products;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class Issue1091IdentityRemediationTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private const string SharedRealmId = "shared-realm";
    private const string WindowsAdSidA = "S-1-5-21-100-200-300-1001";
    private const string WindowsAdSidB = "S-1-5-21-100-200-300-2002";
    private static readonly string EntraOidA = Guid.Parse("11111111-1111-1111-1111-111111111111").ToString("D");

    [Fact]
    public async Task RoleResolver_Cache_Isolates_Tenants_Sharing_Realm_NonScim_And_Scim()
    {
        foreach (var provisioning in new[] { RealmProvisioningMode.Jit, RealmProvisioningMode.Scim })
        {
            var providerCalls = 0;
            var provider = new StubRoleProvider("oidc", (id, _) =>
            {
                providerCalls++;
                var group = id.TenantId == TenantA ? $"realm:{SharedRealmId}:grp-a" : $"realm:{SharedRealmId}:grp-b";
                return Task.FromResult(new ProviderRoleResult(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group },
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
            });

            var syncStore = new StubScimProvisioningStore(epoch: 2);
            using var cache = new RoleResolverCache();
            var resolver = new RoleResolver(
                [provider],
                cache,
                options: Options.Create(new GatewayOptions()),
                scimProvisioningStore: syncStore);
            var realm = CreateRealm(SharedRealmId, IdentityRealmKind.GenericOidc, provisioning: provisioning);

            var resA1 = await resolver.ResolveAsync(CreateIdentity("alice", TenantA, SharedRealmId), realm);
            var resB1 = await resolver.ResolveAsync(CreateIdentity("alice", TenantB, SharedRealmId), realm);
            var resA2 = await resolver.ResolveAsync(CreateIdentity("alice", TenantA, SharedRealmId), realm);

            providerCalls.ShouldBe(2);
            resA1.Groups.ShouldBe([$"realm:{SharedRealmId}:grp-a"]);
            resB1.Groups.ShouldBe([$"realm:{SharedRealmId}:grp-b"]);
            resA2.Groups.ShouldBe([$"realm:{SharedRealmId}:grp-a"]);
        }
    }

    [Fact]
    public async Task GroupSidResolver_ScimSupplement_Cache_Isolates_Tenants_Sharing_Realm()
    {
        var entraRealm = CreateRealm(
            "entra-shared",
            IdentityRealmKind.EntraWorkforce,
            groupSource: RealmGroupSource.Token,
            provisioning: RealmProvisioningMode.Scim,
            issuers: ["https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222/v2.0"]);

        var correlator = new StubScimCorrelator("scim-shared-alice");
        var groupStore = new StubScimGroupStore(tenant => new HashSet<string>(
            tenant == TenantA ? [WindowsAdSidA] : [WindowsAdSidB],
            StringComparer.OrdinalIgnoreCase));
        var syncStore = new StubScimProvisioningStore(epoch: 1, lastContactAt: DateTimeOffset.UtcNow);
        using var roleCache = new RoleResolverCache();

        var sut = new GroupSidResolver(
            roleResolver: null,
            providers: [],
            options: Options.Create(new GatewayOptions()),
            scimCorrelator: correlator,
            scimGroupStore: groupStore,
            scimProvisioningStore: syncStore,
            roleCache: roleCache);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "alice"),
            new Claim("iss", entraRealm.Issuers[0]),
            new Claim("tid", "22222222-2222-2222-2222-222222222222")
        ], "Entra"));

        var resA = await sut.ResolveAsync(principal, entraRealm, TenantA, "Bearer", CancellationToken.None);
        var resB = await sut.ResolveAsync(principal, entraRealm, TenantB, "Bearer", CancellationToken.None);

        resA.Status.ShouldBe(GroupResolutionStatus.Complete);
        resB.Status.ShouldBe(GroupResolutionStatus.Complete);
        resA.GroupSids.Select(s => s.Value).ShouldBe([WindowsAdSidA]);
        resB.GroupSids.Select(s => s.Value).ShouldBe([WindowsAdSidB]);
        groupStore.GetEffectiveCalls.ShouldBe(2);
    }

    [Fact]
    public async Task RoleResolverCache_InvalidateTenant_And_GovernanceEvents_Evict_Only_Target_Tenant()
    {
        var notifier = new TestGovernanceChangeNotifier();
        using var cache = new RoleResolverCache(changeNotifier: notifier);

        var resA = ResolutionWith("realm:r:a");
        var resB = ResolutionWith("realm:r:b");

        await cache.SetAsync(SharedRealmId, $"role:t{TenantA.Value}:e3:alice", resA);
        await cache.SetAsync(SharedRealmId, $"role:t{TenantB.Value}:e3:alice", resB);
        await cache.SetAsync(SharedRealmId, $"scim:t{TenantA.Value}:e1:scim-alice", resA);
        await cache.SetAsync(SharedRealmId, $"scim:t{TenantB.Value}:e1:scim-alice", resB);

        await notifier.EmitAsync(new GovernanceChangeEvent(TenantA.Value, 1, null, DateTimeOffset.UtcNow, SubjectKey: "alice", RealmId: SharedRealmId));
        (await cache.GetAsync(SharedRealmId, $"role:t{TenantA.Value}:e3:alice")).ShouldBeNull();
        (await cache.GetAsync(SharedRealmId, $"role:t{TenantB.Value}:e3:alice")).ShouldNotBeNull();

        await notifier.EmitAsync(new GovernanceChangeEvent(TenantA.Value, 2, null, DateTimeOffset.UtcNow, GroupId: "grp-1"));
        (await cache.GetAsync(SharedRealmId, $"scim:t{TenantA.Value}:e1:scim-alice")).ShouldBeNull();
        (await cache.GetAsync(SharedRealmId, $"scim:t{TenantB.Value}:e1:scim-alice")).ShouldNotBeNull();

        await cache.SetAsync(SharedRealmId, $"role:t{TenantA.Value}:alice", resA);
        cache.InvalidateTenant(TenantA.Value);
        (await cache.GetAsync(SharedRealmId, $"role:t{TenantA.Value}:alice")).ShouldBeNull();
        (await cache.GetAsync(SharedRealmId, $"role:t{TenantB.Value}:e3:alice")).ShouldNotBeNull();
    }

    [Fact]
    public async Task RoleResolverCache_Handles_MaxKeyLengths_And_LiteralEpochPrefixUserKeys()
    {
        using var cache = new RoleResolverCache();
        var maxTenant = new string('a', 64);
        var maxUserKey = new string('u', 256);
        var overUserKey = new string('u', 257);
        var validScoped = $"scim:t{maxTenant}:e9999999999999999999:{maxUserKey}";
        var overScoped = $"role:t{TenantA.Value}:{overUserKey}";
        var literalEpochUserKey = "e0:literal-user";
        var literalCacheKey = $"role:t{TenantA.Value}:{literalEpochUserKey}";

        var sample = ResolutionWith("realm:r:ok");
        await cache.SetAsync(SharedRealmId, validScoped, sample);
        await cache.SetAsync(SharedRealmId, overScoped, sample);
        await cache.SetAsync(SharedRealmId, literalCacheKey, sample);

        (await cache.GetAsync(SharedRealmId, validScoped)).ShouldNotBeNull();
        (await cache.GetAsync(SharedRealmId, overScoped)).ShouldBeNull();
        (await cache.GetAsync(SharedRealmId, literalCacheKey)).ShouldNotBeNull();

        cache.Invalidate(SharedRealmId, literalEpochUserKey);
        (await cache.GetAsync(SharedRealmId, literalCacheKey)).ShouldBeNull();
    }

    [Fact]
    public async Task RoleResolver_DefaultEmptyTenant_DoesNotShare_L1_Cache_Entries()
    {
        var calls = 0;
        var provider = new StubRoleProvider("oidc", (_, _) =>
        {
            calls++;
            return Task.FromResult(new ProviderRoleResult(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"realm:{SharedRealmId}:g{calls}" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        });

        using var cache = new RoleResolverCache();
        var resolver = new RoleResolver([provider], cache, options: Options.Create(new GatewayOptions()));
        var realm = CreateRealm(SharedRealmId, IdentityRealmKind.GenericOidc);

        var r1 = await resolver.ResolveAsync(CreateIdentity("alice", default, SharedRealmId), realm);
        var r2 = await resolver.ResolveAsync(CreateIdentity("alice", default, SharedRealmId), realm);

        calls.ShouldBe(2);
        r1.Groups.ShouldNotBe(r2.Groups);
    }

    [Theory]
    [InlineData(IdentityRealmKind.Customer)]
    [InlineData(IdentityRealmKind.GenericOidc)]
    [InlineData(IdentityRealmKind.EntraExternalId)]
    [InlineData(IdentityRealmKind.Basic)]
    [InlineData(IdentityRealmKind.ForwardAuth)]
    [InlineData(IdentityRealmKind.Pat)]
    public async Task NonWorkforce_Realms_Reject_WindowsAdDomainSids_And_Degrade(IdentityRealmKind realmKind)
    {
        var oidcProvider = new StubRoleProvider("oidc", (_, _) => Task.FromResult(new ProviderRoleResult(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { WindowsAdSidA },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))));

        using var cache = new RoleResolverCache();
        var roleResolver = new RoleResolver([oidcProvider], cache);
        var sidResolver = new GroupSidResolver(roleResolver, [oidcProvider], Options.Create(new GatewayOptions()));
        var realm = CreateRealm(
            "non-wf-realm",
            realmKind,
            groupSource: RealmGroupSource.Resolver,
            resolverChain: ["oidc"]);

        var roleResult = await roleResolver.ResolveAsync(CreateIdentity("alice", TenantA, realm.Id), realm);
        roleResult.IsDegraded.ShouldBeTrue();
        roleResult.Groups.ShouldBeEmpty();

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test"));
        var groupResult = await sidResolver.ResolveAsync(principal, realm, TenantA, "Bearer", CancellationToken.None);
        groupResult.Status.ShouldBe(GroupResolutionStatus.Degraded);
        groupResult.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task GraphRoleProvider_Rejects_WindowsAdDomainSids_On_EntraExternalId_While_Allowing_On_EntraWorkforce()
    {
        var adapter = new StubGraphClientAdapter([WindowsAdSidA, EntraOidA]);
        var provider = new GraphRoleProvider(adapter);
        var issuer = "https://login.microsoftonline.com/33333333-3333-3333-3333-333333333333/v2.0";

        var externalRealm = CreateRealm("ext", IdentityRealmKind.EntraExternalId, issuers: [issuer]);
        var externalIdentity = new ResolvedIdentity(
            "user-oid",
            null,
            null,
            null,
            TenantA,
            externalRealm.Id,
            [new Claim("iss", issuer), new Claim("tid", "33333333-3333-3333-3333-333333333333")]);

        var extResult = await provider.GetRolesAsync(externalIdentity, externalRealm);
        extResult.Failed.ShouldBeTrue();
        extResult.Groups.ShouldNotContain(WindowsAdSidA);

        var wfRealm = CreateRealm("wf", IdentityRealmKind.EntraWorkforce, issuers: [issuer]);
        var wfIdentity = externalIdentity with { RealmId = wfRealm.Id };
        var wfResult = await provider.GetRolesAsync(wfIdentity, wfRealm);
        wfResult.Failed.ShouldBeFalse();
        wfResult.Groups.ShouldContain(WindowsAdSidA);
    }

    [Fact]
    public async Task Auditor_And_Validator_Detect_DataProduct_OwnerGroups_Across_Pagination_And_Async_EmployeeGroups_Via_DI()
    {
        var products = new InMemoryDataProductRepository();
        const string productOwnerGroupId = "g-product-owner";
        const string productOwnerSid = "realm:scim-realm:" + productOwnerGroupId;
        const string employeeGroupId = "g-employee-only";
        var employeeSid = WindowsAdSidB;

        for (var i = 0; i < 205; i++)
        {
            var productId = Guid.NewGuid();
            var owner = i == 203 ? new Sid(productOwnerSid) : new Sid($"realm:scim-realm:other-{i}");
            var created = await products.CreateProductAsync(
                new DataProduct(
                    productId,
                    TenantA,
                    "sales",
                    $"product_{i:D3}",
                    DataProductKind.View,
                    "SELECT 1",
                    "hash",
                    [],
                    [],
                    new Sid("realm:scim-realm:creator"),
                    ProductCreatorAuthority.DataProductAdmin,
                    DataProductStatus.Active,
                    null,
                    LoadSettings.Default,
                    ProductLimits.Default,
                    null,
                    OwnerGroupSid: owner),
                new DataProductVersion(productId, 1, "hash", "SELECT 1", new Sid("realm:scim-realm:creator"), ProductCreatorAuthority.DataProductAdmin, DateTimeOffset.UtcNow));
            created.Status.ShouldBe(ProductWriteStatus.Created);
        }

        var settingsProvider = new StubAccessPolicySettingsProvider(
            TenantA,
            employeeGroupSids: [employeeSid]);
        var auditWriter = new RecordingAuditWriter();

        await using var sp = BuildDiWithOverrides(settingsProvider, products, auditWriter);

        var lookup = sp.GetRequiredService<IEmployeeGroupLookup>();
        lookup.ShouldBeOfType<AccessPolicyEmployeeGroupLookup>();

        var fallbackOnly = new AccessPolicyEmployeeGroupLookup(
            fallbackOptions: Options.Create(new AccessPolicyOptions
            {
                Internal = new InternalAccessPolicySettings { EmployeeGroupSids = [WindowsAdSidA] }
            }));
        (await fallbackOnly.GetEmployeeGroupSidsAsync(TenantA)).ShouldBe([WindowsAdSidA]);

        var auditor = sp.GetRequiredService<PrivilegedGroupChangeAuditor>();
        var validator = sp.GetRequiredService<ScimGroupMembershipValidator>();

        var prodClassification = await auditor.ClassifyGroupAsync(TenantA, "scim-realm", productOwnerGroupId);
        prodClassification.IsPrivileged.ShouldBeTrue();
        prodClassification.Categories.ShouldContain(c => c.Contains("owner_group", StringComparison.Ordinal));

        var empClassification = await auditor.ClassifyGroupAsync(TenantA, "scim-realm", employeeGroupId, groupObjectSid: employeeSid);
        empClassification.IsPrivileged.ShouldBeTrue();
        empClassification.Categories.ShouldContain("employee_group");

        await Should.ThrowAsync<MassMembershipChangeQuarantineException>(() =>
            validator.ValidateMembershipChangeAsync(TenantA, "scim-realm", productOwnerGroupId, currentMemberCount: 10, membersBeingRemovedCount: 5, groupObjectSid: null));

        await Should.ThrowAsync<MassMembershipChangeQuarantineException>(() =>
            validator.ValidateMembershipChangeAsync(TenantA, "scim-realm", employeeGroupId, currentMemberCount: 10, membersBeingRemovedCount: 5, groupObjectSid: employeeSid));

        auditWriter.Entries.ShouldContain(e => e.EventType == "SECURITY_MASS_MEMBERSHIP_REVOCATION_BLOCKED");
    }

    private static ServiceProvider BuildDiWithOverrides(
        IAccessPolicySettingsProvider settingsProvider,
        IDataProductRepository productRepository,
        IAuditWriter auditWriter)
    {
        var services = new ServiceCollection();
        IHostEnvironment env = new TestHostEnvironment(Environments.Development);
        services.AddSingleton(env);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = GovernanceTestConnectionStrings.NonConnectingPostgreSql
            }
        };
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IOptionsMonitor<GatewayOptions>>(new StaticOptionsMonitor<GatewayOptions>(options));
        services.AddSingleton(Options.Create(new IdentityRealmsOptions()));
        services.AddAutherisStorage(options, env);
        services.AddAutherisGovernance(options);
        services.AddAutherisSecurity(options, env);
        services.AddAutherisSqlEngine(options);
        services.AddAutherisExecution(options, env);
        services.AddAutherisCatalog();

        services.AddSingleton(settingsProvider);
        services.AddSingleton(productRepository);
        services.AddSingleton(auditWriter);
        services.AddSingleton<IGovernanceDomainRepository>(new StubGovernanceDomainRepository());
        services.AddSingleton<IFunctionalRightRepository>(new StubFunctionalRightRepository());

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static IdentityRealm CreateRealm(
        string id,
        IdentityRealmKind kind,
        RealmGroupSource groupSource = RealmGroupSource.Resolver,
        RealmProvisioningMode provisioning = RealmProvisioningMode.Jit,
        IReadOnlyList<string>? resolverChain = null,
        IReadOnlyList<string>? issuers = null) =>
        new(
            Id: id,
            Kind: kind,
            Issuers: issuers ?? ["https://issuer.example"],
            MetadataAddress: null,
            Audiences: ["api://autheris"],
            AllowedClientIds: null,
            UserKeyClaim: "sub",
            UpnClaim: "upn",
            TenantBinding: TenantBindingSpecification.Unbound(),
            GroupSource: groupSource,
            ResolverChain: resolverChain ?? ["oidc"],
            Provisioning: provisioning,
            AllowedGatewayRoles: new HashSet<GatewayRole> { GatewayRole.Consumer });

    private static ResolvedIdentity CreateIdentity(string userKey, TenantId tenantId, string realmId) =>
        new(userKey, null, null, null, tenantId, realmId, []);

    private static RoleResolution ResolutionWith(string group) =>
        new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "test");

    private sealed class StubRoleProvider(
        string providerId,
        Func<ResolvedIdentity, IdentityRealm, Task<ProviderRoleResult>> handler) : IRoleProvider
    {
        public string ProviderId => providerId;
        public Task<ProviderRoleResult> GetRolesAsync(ResolvedIdentity identity, IdentityRealm realm, CancellationToken cancellationToken = default) =>
            handler(identity, realm);
    }

    private sealed class StubGraphClientAdapter(IReadOnlyList<string> groups) : IGraphClientAdapter
    {
        public Task<IReadOnlyList<string>> GetTransitiveMemberOfAsync(string userKey, CancellationToken ct = default) =>
            Task.FromResult(groups);

        public Task<IReadOnlyList<string>> GetAppRoleAssignmentsAsync(string userKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubScimCorrelator(string scimUserKey) : IScimIdentityCorrelator
    {
        public ValueTask<CorrelationResult> CorrelateAsync(ClaimsPrincipal normalized, IdentityRealm realm, TenantId tenant, CancellationToken ct = default) =>
            ValueTask.FromResult(new CorrelationResult(CorrelationStatus.Matched, scimUserKey, "sub"));
    }

    private sealed class StubScimProvisioningStore(long epoch, DateTimeOffset? lastContactAt = null) : IScimProvisioningStore
    {
        public Task AddLockAsync(ScimLock lockEntry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsSubjectLockedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<ScimLock>> GetLocksAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimLock>>([]);
        public Task<ScimLockWindow> CountLocksSinceAsync(TenantId tenantId, string realmId, IReadOnlyList<string> sources, DateTimeOffset sinceUtc, int cap, CancellationToken ct = default) =>
            Task.FromResult(new ScimLockWindow(0, null));
        public Task RemoveLockAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ScimRealmSyncState?> GetRealmSyncStateAsync(TenantId tenantId, string realmId, CancellationToken ct = default) =>
            Task.FromResult<ScimRealmSyncState?>(new ScimRealmSyncState(tenantId, realmId, lastContactAt ?? DateTimeOffset.UtcNow, null, epoch));
        public Task UpsertRealmSyncStateAsync(TenantId tenantId, string realmId, DateTimeOffset lastContactAt1, DateTimeOffset? lastWriteAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> IncrementRealmDeprovisionEpochAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult(epoch + 1);
    }

    private sealed class StubScimGroupStore(Func<TenantId, IReadOnlySet<string>> sidsByTenant) : IScimGroupStore
    {
        public int GetEffectiveCalls { get; private set; }
        public Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult<ScimGroup?>(null);
        public Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) => Task.FromResult<ScimGroup?>(null);
        public Task<ScimGroup?> GetGroupByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) => Task.FromResult<ScimGroup?>(null);
        public Task<EffectiveScimGroupsResult> GetEffectiveGroupSidsForUserAsync(TenantId tenantId, string realmId, string userKey, int maxDepth = 32, CancellationToken ct = default)
        {
            GetEffectiveCalls++;
            return Task.FromResult(new EffectiveScimGroupsResult(sidsByTenant(tenantId), false));
        }
        public Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimGroup>>([]);
        public Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetChildGroupIdsAsync(TenantId tenantId, string realmId, string parentGroupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<ScimGroupNestingEdge>> ListGroupNestingEdgesAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimGroupNestingEdge>>([]);
    }

    private sealed class TestGovernanceChangeNotifier : IGovernanceChangeNotifier
    {
        private readonly List<Action<GovernanceChangeEvent>> _handlers = [];

        public Task NotifyAsync(GovernanceChangeEvent change, CancellationToken ct = default) => EmitAsync(change);

        public Task EmitAsync(GovernanceChangeEvent change)
        {
            foreach (var h in _handlers)
            {
                h(change);
            }

            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Action<GovernanceChangeEvent> handler)
        {
            _handlers.Add(handler);
            return new Subscription(_handlers, handler);
        }

        private sealed class Subscription(List<Action<GovernanceChangeEvent>> handlers, Action<GovernanceChangeEvent> handler) : IDisposable
        {
            public void Dispose() => handlers.Remove(handler);
        }
    }

    private sealed class StubAccessPolicySettingsProvider(TenantId targetTenant, IReadOnlyList<string> employeeGroupSids) : IAccessPolicySettingsProvider
    {
        public Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenant, CancellationToken ct = default)
        {
            var opts = new AccessPolicyOptions();
            if (tenant == targetTenant)
            {
                opts.Internal.EmployeeGroupSids.AddRange(employeeGroupSids);
            }

            return Task.FromResult(TenantAccessPolicy.FromDeploymentDefaults(tenant, opts));
        }

        public Task<AccessPolicyUpdateResult> UpdateAsync(TenantId tenant, TenantAccessPolicyUpdate update, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubGovernanceDomainRepository : IGovernanceDomainRepository
    {
        public Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GovernanceDomain>>([]);
        public Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid domainId, CancellationToken ct = default) => Task.FromResult<GovernanceDomain?>(null);
        public Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(TenantId tenant, Guid domainId, int expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AssignTablesAsync(TenantId tenant, Guid domainId, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetProtectionAsync(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetTableProtectionAsync(TenantId tenant, Guid tableId, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubFunctionalRightRepository : IFunctionalRightRepository
    {
        public Task<IReadOnlyList<FunctionalRightMapping>> GetMappingsAsync(TenantId tenantId, string realmId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FunctionalRightMapping>>([]);
        public Task<IReadOnlyList<FunctionalRightMapping>> GetApprovedMappingsForGroupsAndRolesAsync(TenantId tenantId, string realmId, IReadOnlyCollection<string> groupsAndRoles, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FunctionalRightMapping>>([]);
        public Task<FunctionalRightMapping?> GetMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, CancellationToken ct = default) =>
            Task.FromResult<FunctionalRightMapping?>(null);
        public Task CreateMappingAsync(FunctionalRightMapping mapping, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApproveMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, string approvedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
