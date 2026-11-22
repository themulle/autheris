namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Rights;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Providers;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

[Trait("Category", "Security")]
public sealed class GroupOverageFailClosedTests
{
    private const string UserSid = "S-1-5-21-1-2-3-1001";
    private const string SidA = "S-1-5-21-1-2-3-1105";
    private const string SidB = "S-1-5-21-1-2-3-1106";
    private const string SidC = "S-1-5-21-1-2-3-1107";
    private const string TargetGroupSid = "S-1-5-21-1-2-3-2001";
    private static readonly TenantId Tenant = new("tenant-overage");
    private static readonly TableIdentifier OrdersTable = new("sales", "public", "orders");

    private static IdentityRealm EntraTokenRealm() => new(
        "entra-token",
        IdentityRealmKind.EntraWorkforce,
        TenantBindingSpecification.Fixed(Tenant),
        RealmGroupSource.Token,
        RealmProvisioningMode.None,
        issuers: ["https://login.microsoftonline.com/tenant/v2.0"]);

    private static IdentityRealm ResolverRealm(params string[] chain) => new(
        "entra-resolver",
        IdentityRealmKind.EntraWorkforce,
        TenantBindingSpecification.Fixed(Tenant),
        RealmGroupSource.Resolver,
        RealmProvisioningMode.None,
        issuers: ["https://login.microsoftonline.com/tenant/v2.0"],
        resolverChain: chain);

    private static ClaimsPrincipal Token(params Claim[] claims) =>
        new(new ClaimsIdentity([new Claim("oid", "user-oid-1"), new Claim(ClaimTypes.PrimarySid, UserSid), .. claims], "Bearer"));

    private static IOptions<GatewayOptions> BuildOptions(int maxGroups = 1024, bool bypassConsent = false) =>
        Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            Authentication = new AuthenticationOptions { MaxGroupSidsPerCaller = maxGroups },
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = bypassConsent }
        });

    private static GroupSidResolver CreateGroupResolver(IEnumerable<IRoleProvider>? providers = null, int maxGroups = 1024)
    {
        var list = (providers ?? []).ToList();
        var options = BuildOptions(maxGroups);
        var roleResolver = new RoleResolver(list, new RoleResolverCache(), null, options);
        return new GroupSidResolver(roleResolver, list, options);
    }

    [Fact]
    public async Task Overage_without_graph_degraded()
    {
        var sut = CreateGroupResolver();
        var realm = EntraTokenRealm();

        Claim[][] overageClaimSets =
        [
            [new Claim("hasgroups", "true")],
            [new Claim("hasgroups", "1")],
            [new Claim("hasgroups", " true ")],
            [new Claim("hasgroups", "")],
            [new Claim("hasgroups", "   ")],
            [new Claim("hasgroups", "unexpected-value")],
            [new Claim("_claim_names", "{\"groups\":\"src1\"}")],
            [new Claim("_claim_names", "{\"GROUPS\":\"src1\"}")],
            [new Claim("_claim_names", "")],
            [new Claim("_claim_names", "   ")],
            [new Claim("_claim_names", "{not-json")],
            [new Claim("_claim_names", "[\"groups\"]")],
            [new Claim("_claim_names", "\"groups\"")],
            [new Claim("_claim_names", "123")],
            [new Claim("_claim_names", "null")],
            [new Claim("_claim_sources", "{\"src1\":{\"endpoint\":\"https://graph.windows.net/v1/users/u/getMemberObjects\"}}")],
            [new Claim("_claim_sources", "{\"src1\":{\"endpoint\":\"https://graph.windows.net/v1/users/u/getMemberObjects\"}}"), new Claim("groups", "   ")],
        ];

        foreach (var overageClaims in overageClaimSets)
        {
            var hasGroupsPresent = overageClaims.Any(c => c.Type == "groups");
            var withPartialGroups = hasGroupsPresent
                ? Token(overageClaims)
                : Token([.. overageClaims, new Claim("groups", SidA)]);

            // When _claim_sources is tested with omitted groups, keep groups omitted (or blank) on the primary token.
            var testPrincipal = overageClaims.Any(c => c.Type == "_claim_sources")
                ? Token(overageClaims)
                : withPartialGroups;

            GroupSidResolver.HasGroupOverage(testPrincipal).ShouldBeTrue();
            var result = await sut.ResolveAsync(testPrincipal, realm, Tenant, "Bearer", CancellationToken.None);
            result.Status.ShouldBe(GroupResolutionStatus.Degraded);
            result.GroupSids.ShouldBeEmpty();
        }

        // Non-overage controls: false / "0" hasgroups and non-groups _claim_names such as {"my_groups":"src1"} must not false-positive.
        Claim[][] nonOverageClaimSets =
        [
            [new Claim("hasgroups", "false"), new Claim("groups", SidA)],
            [new Claim("hasgroups", " 0 "), new Claim("groups", SidA)],
            [new Claim("_claim_names", "{\"my_groups\":\"src1\"}"), new Claim("groups", SidA)],
            [new Claim("_claim_names", "{}"), new Claim("groups", SidA)],
            [new Claim("_claim_names", "{\"my_groups\":\"src1\"}"), new Claim("_claim_sources", "{\"src1\":{\"endpoint\":\"https://example.test\"}}"), new Claim("groups", SidA)],
        ];

        foreach (var claims in nonOverageClaimSets)
        {
            var principal = Token(claims);
            GroupSidResolver.HasGroupOverage(principal).ShouldBeFalse();
            var result = await sut.ResolveAsync(principal, realm, Tenant, "Bearer", CancellationToken.None);
            result.Status.ShouldBe(GroupResolutionStatus.Complete);
            result.GroupSids.ShouldBe([new Sid(SidA)]);
        }
    }

    [Fact]
    public async Task Graph_failure_degraded()
    {
        var realm = EntraTokenRealm();
        var overageWithPartial = Token(new Claim("hasgroups", "true"), new Claim("groups", SidA));

        // 1. Graph adapter throws 429 throttle exception.
        var throttledResolver = CreateGroupResolver([
            new GraphRoleProvider(new ControlledGraphAdapter
            {
                OnTransitiveMemberOf = (_, _) => throw new GraphThrottledException(TimeSpan.FromSeconds(10), "429 Too Many Requests")
            })
        ]);
        var throttled = await throttledResolver.ResolveAsync(overageWithPartial, realm, Tenant, "Bearer", CancellationToken.None);
        throttled.Status.ShouldBe(GroupResolutionStatus.Degraded);
        throttled.GroupSids.ShouldBeEmpty();

        // 2. Graph adapter times out (respects provider timeout CancellationToken without using Task.Delay).
        var timeoutResolver = CreateGroupResolver([
            new GraphRoleProvider(
                new ControlledGraphAdapter
                {
                    OnTransitiveMemberOf = async (_, ct) =>
                    {
                        var tcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
                        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                        return await tcs.Task.ConfigureAwait(false);
                    }
                },
                timeout: TimeSpan.FromMilliseconds(20))
        ]);
        var timedOut = await timeoutResolver.ResolveAsync(overageWithPartial, realm, Tenant, "Bearer", CancellationToken.None);
        timedOut.Status.ShouldBe(GroupResolutionStatus.Degraded);
        timedOut.GroupSids.ShouldBeEmpty();

        // 3. Graph adapter throws general exception -> trips circuit breaker -> subsequent call fails closed without calling adapter.
        var breakerAdapter = new ControlledGraphAdapter
        {
            OnTransitiveMemberOf = (_, _) => throw new InvalidOperationException("graph outage")
        };
        var breakerGraph = new GraphRoleProvider(breakerAdapter, failureThreshold: 1, circuitBreakDuration: TimeSpan.FromMinutes(1));
        var breakerResolver = CreateGroupResolver([breakerGraph]);

        var firstFail = await breakerResolver.ResolveAsync(overageWithPartial, realm, Tenant, "Bearer", CancellationToken.None);
        firstFail.Status.ShouldBe(GroupResolutionStatus.Degraded);
        firstFail.GroupSids.ShouldBeEmpty();
        breakerGraph.IsCircuitBreakerOpen.ShouldBeTrue();

        var callsAfterTrip = breakerAdapter.TransitiveCalls;
        var openCircuit = await breakerResolver.ResolveAsync(overageWithPartial, realm, Tenant, "Bearer", CancellationToken.None);
        openCircuit.Status.ShouldBe(GroupResolutionStatus.Degraded);
        openCircuit.GroupSids.ShouldBeEmpty();
        breakerAdapter.TransitiveCalls.ShouldBe(callsAfterTrip);

        // 4. Custom Graph IRoleProvider returning FailedResult, null, non-canonical group, or throwing directly.
        IRoleProvider[] failingProviders =
        [
            new FakeRoleProvider(GraphRoleProvider.Id, _ => new ProviderRoleResult(new HashSet<string> { SidB }, new HashSet<string>(), new Dictionary<string, string>()) { Failed = true }),
            new NullResultRoleProvider(GraphRoleProvider.Id),
            new FakeRoleProvider(GraphRoleProvider.Id, _ => new ProviderRoleResult(new HashSet<string> { SidB, "not-a-sid" }, new HashSet<string>(), new Dictionary<string, string>())),
            new ThrowingRoleProvider(GraphRoleProvider.Id, () => new HttpRequestException("503"))
        ];

        foreach (var provider in failingProviders)
        {
            var resolver = CreateGroupResolver([provider]);
            var res = await resolver.ResolveAsync(overageWithPartial, realm, Tenant, "Bearer", CancellationToken.None);
            res.Status.ShouldBe(GroupResolutionStatus.Degraded);
            res.GroupSids.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Cap_exceeded_degraded()
    {
        var realm = EntraTokenRealm();
        var overageToken = Token(new Claim("hasgroups", "true"), new Claim("groups", SidA));

        // 1. GraphRoleProvider cap exceeded.
        var graphCapAdapter = new ControlledGraphAdapter
        {
            OnTransitiveMemberOf = (_, _) => Task.FromResult<IReadOnlyList<string>>([SidA, SidB, SidC])
        };
        var graphCapResolver = CreateGroupResolver([new GraphRoleProvider(graphCapAdapter, maxGroups: 2)], maxGroups: 1024);
        var graphCapResult = await graphCapResolver.ResolveAsync(overageToken, realm, Tenant, "Bearer", CancellationToken.None);
        graphCapResult.Status.ShouldBe(GroupResolutionStatus.Degraded);
        graphCapResult.GroupSids.ShouldBeEmpty();

        // 2. GroupSidResolver MaxGroupSidsPerCaller cap exceeded on Graph provider return.
        var sidCapProvider = new FakeRoleProvider(
            GraphRoleProvider.Id,
            _ => new ProviderRoleResult(new HashSet<string> { SidA, SidB, SidC }, new HashSet<string>(), new Dictionary<string, string>()));
        var sidCapResolver = CreateGroupResolver([sidCapProvider], maxGroups: 2);
        var sidCapResult = await sidCapResolver.ResolveAsync(overageToken, realm, Tenant, "Bearer", CancellationToken.None);
        sidCapResult.Status.ShouldBe(GroupResolutionStatus.Degraded);
        sidCapResult.GroupSids.ShouldBeEmpty();

        // 3. RoleResolver MaxGroupSidsPerCaller cap exceeded -> both Groups and AppRoles are cleared and not cached.
        var cache = new RoleResolverCache();
        var chainProvider = new FakeRoleProvider(
            "p1",
            _ => new ProviderRoleResult(new HashSet<string> { SidA, SidB, SidC }, new HashSet<string> { "DataAdmin" }, new Dictionary<string, string>()));
        var roleResolver = new RoleResolver([chainProvider], cache, null, BuildOptions(maxGroups: 2));
        var identity = new ResolvedIdentity("user-oid-1", null, null, null, Tenant, "entra-resolver", []);

        var roleResolution = await roleResolver.ResolveAsync(identity, ResolverRealm("p1"), CancellationToken.None);
        roleResolution.IsDegraded.ShouldBeTrue();
        roleResolution.Groups.ShouldBeEmpty();
        roleResolution.AppRoles.ShouldBeEmpty();
        (await cache.GetAsync("entra-resolver", "role:user-oid-1")).ShouldBeNull();
    }

    [Fact]
    public async Task Pdp_denies_on_degraded()
    {
        var metadata = new TableMetadata
        {
            Identifier = OrdersTable,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "public", TableName = "orders", SourceType = "PostgreSql", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
        };

        // Explicit user-level Allow consent: proves PDP denies degraded callers even when user has direct allow (never "0 groups").
        var userConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = metadata.Table.Id,
            TableIdentifier = OrdersTable,
            TenantId = Tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(UserSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
        };

        var consents = new InMemoryConsentRepository(userConsent);
        var pdp = new AccessDecisionPoint(
            BuildOptions(),
            new ConsentResolutionService(),
            consents,
            NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance);

        // Control: complete status allows access.
        var completeSubject = new SecurityPrincipalContext
        {
            UserSid = new Sid(UserSid),
            ResolvedTenant = Tenant,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            IsAuthenticated = true
        };
        (await pdp.DecideAsync(new AccessRequest(completeSubject, metadata, AccessOperation.Read, AccessTransport.Rest))).IsAllowed.ShouldBeTrue();

        // 1. Authenticated caller with GroupResolutionStatus.Degraded is denied.
        var degradedSubject = completeSubject with { GroupResolutionStatus = GroupResolutionStatus.Degraded };
        var degradedDecision = await pdp.DecideAsync(new AccessRequest(degradedSubject, metadata, AccessOperation.Read, AccessTransport.Rest));
        degradedDecision.IsAllowed.ShouldBeFalse();
        degradedDecision.DeniedReasons.ShouldContain(r => r.Contains("Group resolution unavailable", StringComparison.OrdinalIgnoreCase));

        // 2. Even when consent checks are bypassed (public/open fallback), degraded group status is denied.
        var bypassPdp = new AccessDecisionPoint(
            BuildOptions(bypassConsent: true),
            new ConsentResolutionService(),
            consents,
            NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance);
        (await bypassPdp.DecideAsync(new AccessRequest(degradedSubject, metadata, AccessOperation.Read, AccessTransport.Rest))).IsAllowed.ShouldBeFalse();

        // 3. Principal or Claims carrying degraded marker (even if status field was set Complete) is denied.
        var degradedPrincipal = Token(new Claim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded));
        var viaPrincipal = completeSubject with { Principal = degradedPrincipal };
        (await pdp.DecideAsync(new AccessRequest(viaPrincipal, metadata, AccessOperation.Read, AccessTransport.Rest))).IsAllowed.ShouldBeFalse();

        var viaClaims = completeSubject with { Principal = null, Claims = [new Claim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded)] };
        (await bypassPdp.DecideAsync(new AccessRequest(viaClaims, metadata, AccessOperation.Read, AccessTransport.Rest))).IsAllowed.ShouldBeFalse();

        // 4. Even when no policy/consent repository is wired (consentBypassed public fallback), degraded Claims or status still denies.
        var publicBypassPdp = new AccessDecisionPoint(
            BuildOptions(bypassConsent: true),
            resolutionService: null,
            consentRepository: null);
        (await publicBypassPdp.DecideAsync(new AccessRequest(degradedSubject, metadata, AccessOperation.Read, AccessTransport.Rest))).IsAllowed.ShouldBeFalse();
        (await publicBypassPdp.DecideAsync(new AccessRequest(viaClaims, metadata, AccessOperation.Read, AccessTransport.Rest))).IsAllowed.ShouldBeFalse();

        // 5. Write operations with degraded status or degraded claims are denied.
        (await pdp.DecideAsync(new AccessRequest(degradedSubject, metadata, AccessOperation.Update, AccessTransport.WebSql, Columns: ["id"]))).IsAllowed.ShouldBeFalse();
        (await pdp.DecideAsync(new AccessRequest(viaClaims, metadata, AccessOperation.Update, AccessTransport.WebSql, Columns: ["id"]))).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Sod_membership_undeterminable_denies()
    {
        var groupStore = NullFake.Create<IScimGroupStore>((method, _) =>
        {
            if (method.Name == nameof(IScimGroupStore.GetEffectiveGroupSidsForUserAsync))
            {
                return Task.FromResult(new EffectiveScimGroupsResult(new HashSet<string>(), MaxDepthExceeded: false, HasNonCanonicalSid: false));
            }
            if (method.Name == nameof(IScimGroupStore.GetGroupMembershipsForUserAsync))
            {
                return Task.FromResult<IReadOnlyList<string>>([]);
            }
            return NullFake.UseDefault;
        });
        var sut = new SeparationOfDutiesService(groupStore);
        var actor = new Sid(UserSid);

        // Control: complete pipeline-verified principal not in target group is allowed.
        var completePrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, UserSid),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
        ], "Bearer"));
        var completeContext = new SecurityPrincipalContext
        {
            UserSid = actor,
            ResolvedTenant = Tenant,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };
        (await sut.CheckGroupMembershipAsync(Tenant, completePrincipal, actor, completeContext, null, TargetGroupSid)).Allowed.ShouldBeTrue();

        // 1. SecurityPrincipalContext with GroupResolutionStatus.Degraded denies fail-closed.
        var degradedContext = completeContext with { GroupResolutionStatus = GroupResolutionStatus.Degraded };
        (await sut.CheckGroupMembershipAsync(Tenant, null, actor, degradedContext, null, TargetGroupSid)).Allowed.ShouldBeFalse();

        // 2. Principal with pipeline degraded status or role-resolution degraded claim denies fail-closed.
        var statusDegradedPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, UserSid),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Degraded)
        ], "Bearer"));
        (await sut.CheckGroupMembershipAsync(Tenant, statusDegradedPrincipal, actor, null, null, TargetGroupSid)).Allowed.ShouldBeFalse();

        var roleDegradedPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, UserSid),
            new Claim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded)
        ], "Bearer"));
        (await sut.CheckGroupMembershipAsync(Tenant, roleDegradedPrincipal, actor, null, null, TargetGroupSid)).Allowed.ShouldBeFalse();

        // 3. Pipeline-marked principal without a Complete status claim denies fail-closed.
        var missingStatusPipelinePrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, UserSid),
            GroupResolutionClaims.CreateMarker()
        ], "Bearer"));
        (await sut.CheckGroupMembershipAsync(Tenant, missingStatusPipelinePrincipal, actor, null, null, TargetGroupSid)).Allowed.ShouldBeFalse();

        // 4. Unprocessed principal carrying overage hints denies fail-closed.
        var overagePrincipal = Token(new Claim("hasgroups", "true"));
        (await sut.CheckGroupMembershipAsync(Tenant, overagePrincipal, actor, null, null, TargetGroupSid)).Allowed.ShouldBeFalse();

        // 5. CallerContext with Complete status but degraded Principal or Claims still denies fail-closed.
        var maskedDegradedContext = completeContext with { Principal = roleDegradedPrincipal };
        (await sut.CheckGroupMembershipAsync(Tenant, null, actor, maskedDegradedContext, null, TargetGroupSid)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Role_from_group_ignores_partial_set()
    {
        // 1. RoleResolver: when one provider fails or is missing, surviving provider's Groups and AppRoles are BOTH discarded.
        var cache = new RoleResolverCache();
        var survivingProvider = new FakeRoleProvider(
            "p2",
            _ => new ProviderRoleResult(
                new HashSet<string> { SidB },
                new HashSet<string> { "DataAdmin", "GovernanceAdmin" },
                new Dictionary<string, string> { ["dept"] = "finance" }));
        var failingProvider = new FakeRoleProvider("p1", _ => ProviderRoleResult.FailedResult);
        var throwingProvider = new ThrowingRoleProvider("p1", () => new InvalidOperationException("down"));

        var roleResolverFail = new RoleResolver([failingProvider, survivingProvider], cache, null, BuildOptions());
        var resolvedIdentity = new ResolvedIdentity("user-oid-1", null, null, null, Tenant, "entra-resolver", []);
        var failedRes = await roleResolverFail.ResolveAsync(resolvedIdentity, ResolverRealm("p1", "p2"), CancellationToken.None);
        failedRes.IsDegraded.ShouldBeTrue();
        failedRes.Groups.ShouldBeEmpty();
        failedRes.AppRoles.ShouldBeEmpty();

        var roleResolverThrow = new RoleResolver([throwingProvider, survivingProvider], cache, null, BuildOptions());
        var thrownRes = await roleResolverThrow.ResolveAsync(resolvedIdentity, ResolverRealm("p1", "p2"), CancellationToken.None);
        thrownRes.IsDegraded.ShouldBeTrue();
        thrownRes.Groups.ShouldBeEmpty();
        thrownRes.AppRoles.ShouldBeEmpty();

        var roleResolverMissing = new RoleResolver([survivingProvider], cache, null, BuildOptions());
        var missingRes = await roleResolverMissing.ResolveAsync(resolvedIdentity, ResolverRealm("missing-provider", "p2"), CancellationToken.None);
        missingRes.IsDegraded.ShouldBeTrue();
        missingRes.Groups.ShouldBeEmpty();
        missingRes.AppRoles.ShouldBeEmpty();

        // 2. GroupSidResolver on Entra overage: ignores partial token groups when Graph succeeds (only Graph SIDs returned),
        //    and ignores partial token groups when Graph fails (empty + Degraded).
        var overageTokenWithPartial = Token(new Claim("hasgroups", "true"), new Claim("groups", SidA));
        var graphSuccessResolver = CreateGroupResolver([
            new FakeRoleProvider(GraphRoleProvider.Id, _ => new ProviderRoleResult(new HashSet<string> { SidB }, new HashSet<string>(), new Dictionary<string, string>()))
        ]);
        var overageOk = await graphSuccessResolver.ResolveAsync(overageTokenWithPartial, EntraTokenRealm(), Tenant, "Bearer", CancellationToken.None);
        overageOk.Status.ShouldBe(GroupResolutionStatus.Complete);
        overageOk.GroupSids.ShouldBe([new Sid(SidB)]);

        // 3. IdentityPipeline: when RoleResolver returns Degraded (even with partial Groups/Roles on a custom IRoleResolver),
        //    AddRolesAndRightsAsync drops/skips partial roles and functional rights.
        var realm = ResolverRealm("p1", "p2");
        var registry = new FakeIdentityRealmRegistry(realm);
        var partialDegradedRoleResolver = new FixedRoleResolver(new RoleResolution(
            new HashSet<string> { SidB },
            new HashSet<string> { "DataAdmin" },
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "degraded")
        {
            Status = RoleResolutionStatus.Degraded
        });
        var rightMapper = new RecordingFunctionalRightMapper(FunctionalRight.DataExport);
        var pipeline = new IdentityPipeline(
            registry,
            CreateGroupResolver([failingProvider, survivingProvider]),
            partialDegradedRoleResolver,
            rightMapper,
            options: BuildOptions());

        var inbound = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("oid", "user-oid-1"),
            new Claim(ClaimTypes.PrimarySid, UserSid),
            new Claim(RequireFunctionalRightHandler.FunctionalRightClaimType, FunctionalRight.GovernanceAdmin.Value)
        ], "Bearer"));

        var processed = await pipeline.ProcessAsync(inbound, "Bearer", null, CancellationToken.None);
        GroupResolutionClaims.Read(processed).ShouldBe(GroupResolutionStatus.Degraded);
        processed.HasClaim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded).ShouldBeTrue();
        processed.GetGroupSids().ShouldBeEmpty();
        processed.FindAll(ClaimTypes.Role).ShouldBeEmpty();
        processed.FindAll(RequireFunctionalRightHandler.FunctionalRightClaimType).ShouldBeEmpty();
        rightMapper.Calls.ShouldBe(0);
    }

    private sealed class ControlledGraphAdapter : IGraphClientAdapter
    {
        public Func<string, CancellationToken, Task<IReadOnlyList<string>>> OnTransitiveMemberOf { get; init; } =
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]);

        public int TransitiveCalls { get; private set; }

        public Task<IReadOnlyList<string>> GetTransitiveMemberOfAsync(string userKey, CancellationToken ct = default)
        {
            TransitiveCalls++;
            return OnTransitiveMemberOf(userKey, ct);
        }

        public Task<IReadOnlyList<string>> GetAppRoleAssignmentsAsync(string userKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NullResultRoleProvider(string id) : IRoleProvider
    {
        public string ProviderId => id;

        public Task<ProviderRoleResult> GetRolesAsync(ResolvedIdentity identity, IdentityRealm realm, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProviderRoleResult>(null!);
    }

    private sealed class ThrowingRoleProvider(string id, Func<Exception> createException) : IRoleProvider
    {
        public string ProviderId => id;

        public Task<ProviderRoleResult> GetRolesAsync(ResolvedIdentity identity, IdentityRealm realm, CancellationToken cancellationToken = default) =>
            throw createException();
    }

    private sealed class FixedRoleResolver(RoleResolution resolution) : IRoleResolver
    {
        public Task<RoleResolution> ResolveAsync(ResolvedIdentity identity, IdentityRealm realm, CancellationToken cancellationToken = default) =>
            Task.FromResult(resolution);

        public Task<RoleResolution> ResolveRolesAsync(ResolvedIdentity identity, CancellationToken cancellationToken = default) =>
            Task.FromResult(resolution);
    }

    private sealed class RecordingFunctionalRightMapper(params FunctionalRight[] rights) : IFunctionalRightMapper
    {
        public int Calls { get; private set; }

        public Task<IReadOnlySet<FunctionalRight>> GetRightsForCallerAsync(ClaimsPrincipal principal, TenantId tenantId, string realmId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlySet<FunctionalRight>>(rights.ToHashSet());
        }

        public Task<IReadOnlySet<FunctionalRight>> GetRightsForGroupsAndRolesAsync(TenantId tenantId, string realmId, IReadOnlyCollection<string> groupsAndRoles, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlySet<FunctionalRight>>(rights.ToHashSet());
        }

        public Task<bool> HasRightAsync(ClaimsPrincipal principal, TenantId tenantId, string realmId, FunctionalRight right, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task CreateMappingAsync(FunctionalRightMapping mapping, CancellationToken ct = default) => Task.CompletedTask;

        public Task<FunctionalRightMapping> ProposeMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight right, RecertClass recertClass, string createdBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ApproveMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight right, string approverId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight right, string deletedBy, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
