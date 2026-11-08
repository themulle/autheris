using System.Diagnostics.Metrics;
using System.Security.Claims;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Providers;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class GroupSidResolverTests
{
    private const string SidA = "S-1-5-21-1-2-3-1105";
    private const string SidB = "S-1-5-21-1-2-3-1106";
    private const string SidC = "S-1-5-21-1-2-3-1107";
    private const string ObjectId = "11223344-5566-7788-99aa-bbccddeeff00";
    private const string CloudSid = "S-1-12-1-287454020-2005423462-3434850969-16772829";
    private static readonly TenantId Tenant = new("tenant-1");

    private static IdentityRealm Realm(RealmGroupSource source, params string[] chain) => new(
        "realm-1", IdentityRealmKind.EntraWorkforce, TenantBindingSpecification.Fixed(Tenant), source, RealmProvisioningMode.Scim,
        issuers: ["https://issuer.example.org"], resolverChain: chain);

    private static ClaimsPrincipal Token(params Claim[] claims) =>
        new(new ClaimsIdentity([new Claim("oid", "user-oid-1"), .. claims], "Bearer"));

    private static IOptions<GatewayOptions> Options(int maxGroups = 1024) => Microsoft.Extensions.Options.Options.Create(new GatewayOptions
    {
        Authentication = new AuthenticationOptions
        {
            MaxGroupSidsPerCaller = maxGroups,
            Adfs = new AdfsAuthOptions { Enabled = true, Authority = "https://adfs.example.org/adfs", Audience = "urn:a", AllowedClientIds = ["c"] }
        }
    });

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

    private static GroupSidResolver Resolver(IEnumerable<IRoleProvider>? providers = null, int maxGroups = 1024, bool windows = false, ILogger<GroupSidResolver>? logger = null)
    {
        var list = (providers ?? []).ToList();
        var options = Options(maxGroups);
        var roleResolver = new RoleResolver(list, new RoleResolverCache(), null, options);
        return new GroupSidResolver(roleResolver, list, options, logger, isWindowsHost: windows);
    }

    private static async Task<GroupResolution> ResolveAsync(GroupSidResolver sut, ClaimsPrincipal principal, IdentityRealm realm, string scheme = "Bearer") =>
        await sut.ResolveAsync(principal, realm, Tenant, scheme, CancellationToken.None);

    private static string[] Values(GroupResolution r) => r.GroupSids.Select(s => s.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Entra_sid_groups_are_taken()
    {
        var result = await ResolveAsync(Resolver(), Token(new Claim("groups", SidA), new Claim("groups", SidB)), Realm(RealmGroupSource.Token));

        result.Status.ShouldBe(GroupResolutionStatus.Complete);
        Values(result).ShouldBe([SidA, SidB]);
    }

    [Fact]
    public async Task Entra_guid_groups_become_cloud_sids()
    {
        var result = await ResolveAsync(Resolver(), Token(new Claim("groups", ObjectId)), Realm(RealmGroupSource.Token));

        result.Status.ShouldBe(GroupResolutionStatus.Complete);
        Values(result).ShouldBe([CloudSid]);
    }

    [Fact]
    public async Task Entra_non_guid_non_sid_group_is_rejected_and_degraded()
    {
        var result = await ResolveAsync(Resolver(), Token(new Claim("groups", SidA), new Claim("groups", "finance-team")), Realm(RealmGroupSource.Token));

        result.Status.ShouldBe(GroupResolutionStatus.Degraded);
        result.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task Entra_overage_uses_graph()
    {
        var adapter = Substitute.For<IGraphClientAdapter>();
        adapter.GetTransitiveMemberOfAsync("user-oid-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([SidA, ObjectId]));
        adapter.GetAppRoleAssignmentsAsync("user-oid-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));
        var sut = Resolver([new GraphRoleProvider(adapter)]);

        var result = await ResolveAsync(sut, Token(new Claim("hasgroups", "true")), Realm(RealmGroupSource.Token));

        result.Status.ShouldBe(GroupResolutionStatus.Complete);
        Values(result).ShouldBe([CloudSid, SidA]);
        await adapter.Received(1).GetTransitiveMemberOfAsync("user-oid-1", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("hasgroups", "true")]
    [InlineData("_claim_names", "{\"groups\":\"src1\"}")]
    public async Task Entra_overage_without_graph_is_degraded(string claimType, string claimValue)
    {
        var result = await ResolveAsync(Resolver(), Token(new Claim("groups", SidA), new Claim(claimType, claimValue)), Realm(RealmGroupSource.Token));

        result.Status.ShouldBe(GroupResolutionStatus.Degraded);
        result.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task Entra_overage_with_open_breaker_is_degraded()
    {
        var adapter = Substitute.For<IGraphClientAdapter>();
        adapter.GetTransitiveMemberOfAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<string>>>(_ => throw new InvalidOperationException("down"));
        var graph = new GraphRoleProvider(adapter, failureThreshold: 1);
        var sut = Resolver([graph]);
        var principal = Token(new Claim("hasgroups", "true"));

        (await ResolveAsync(sut, principal, Realm(RealmGroupSource.Token))).Status.ShouldBe(GroupResolutionStatus.Degraded);
        graph.IsCircuitBreakerOpen.ShouldBeTrue();
        adapter.ClearReceivedCalls();

        (await ResolveAsync(sut, principal, Realm(RealmGroupSource.Token))).Status.ShouldBe(GroupResolutionStatus.Degraded);
        await adapter.DidNotReceiveWithAnyArgs().GetTransitiveMemberOfAsync(default!, default);
    }

    [Fact]
    public async Task Graph_page_beyond_cap_is_degraded()
    {
        var adapter = Substitute.For<IGraphClientAdapter>();
        adapter.GetTransitiveMemberOfAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([SidA, SidB, SidC]));
        adapter.GetAppRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));
        var sut = Resolver([new GraphRoleProvider(adapter, maxGroups: 2)], maxGroups: 2);

        var result = await ResolveAsync(sut, Token(new Claim("hasgroups", "true")), Realm(RealmGroupSource.Token));

        result.Status.ShouldBe(GroupResolutionStatus.Degraded);
        result.GroupSids.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("BearerAdfs")]
    [InlineData("Bearer")]
    public async Task Adfs_groupsid_claims_are_taken(string scheme)
    {
        var claims = new[]
        {
            new Claim("iss", "https://adfs.example.org/adfs/services/trust"),
            new Claim(ClaimTypes.GroupSid, SidA, ClaimValueTypes.String, "https://adfs.example.org/adfs"),
            new Claim("groupsid", SidB),
            new Claim("groups", SidC)
        };

        var result = await ResolveAsync(Resolver(), Token(claims), Realm(RealmGroupSource.Token), scheme);

        result.Status.ShouldBe(GroupResolutionStatus.Complete);
        Values(result).ShouldBe([SidA, SidB]);
    }

    [Fact]
    public async Task Negotiate_pac_groups_are_taken()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-1001"), new Claim(ClaimTypes.GroupSid, SidA, ClaimValueTypes.String, "AD AUTHORITY"), new Claim(ClaimTypes.GroupSid, SidB, ClaimValueTypes.String, "AD AUTHORITY")],
            "Kerberos"));

        var result = await ResolveAsync(Resolver(windows: true), principal, Realm(RealmGroupSource.Token), "Negotiate");

        result.Status.ShouldBe(GroupResolutionStatus.Complete);
        Values(result).ShouldBe([SidA, SidB]);
    }

    [Fact]
    public async Task Ldaps_tokenGroups_resolve_transitively()
    {
        var adapter = Substitute.For<ILdapDirectoryAdapter>();
        adapter.FindUserDnsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>(["CN=u,DC=corp"]));
        SidFormat.TryToBinary(SidA, out var a);
        SidFormat.TryToBinary(SidB, out var b);
        adapter.ReadTokenGroupsAsync("CN=u,DC=corp", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<byte[]>>([a, b]));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-1001"), new Claim("sub", "krb-user")], "Kerberos"));

        var result = await ResolveAsync(Resolver([new LdapsRoleProvider(adapter)]), principal, Realm(RealmGroupSource.Token, LdapsRoleProvider.Id), "Negotiate");

        result.Status.ShouldBe(GroupResolutionStatus.Complete);
        Values(result).ShouldBe([SidA, SidB]);
    }

    [Fact]
    public async Task Scim_store_groups_resolve_to_sids()
    {
        var sut = Resolver([Provider("scim-store", () => Groups(SidA, SidB))]);

        var result = await ResolveAsync(sut, Token(), Realm(RealmGroupSource.Resolver, "scim-store"));

        result.Status.ShouldBe(GroupResolutionStatus.Complete);
        Values(result).ShouldBe([SidA, SidB]);
        result.Source.ShouldBe("resolver");
    }

    [Fact]
    public async Task Resolver_groups_are_not_realm_prefixed()
    {
        var sut = Resolver([Provider("scim-store", () => Groups(SidA))]);

        var result = await ResolveAsync(sut, Token(), Realm(RealmGroupSource.Resolver, "scim-store"));

        Values(result).ShouldBe([SidA]);
        result.GroupSids.ShouldAllBe(s => !s.Value.Contains('|'));
    }

    [Fact]
    public async Task Resolver_non_canonical_value_degrades()
    {
        var sut = Resolver([Provider("scim-store", () => Groups(SidA, "finance-team"))]);

        var result = await ResolveAsync(sut, Token(), Realm(RealmGroupSource.Resolver, "scim-store"));

        result.Status.ShouldBe(GroupResolutionStatus.Degraded);
        result.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolver_unknown_provider_id_is_degraded()
    {
        var sut = Resolver([Provider("scim-store", () => Groups(SidA))]);

        var result = await ResolveAsync(sut, Token(), Realm(RealmGroupSource.Resolver, "scim-store", "does-not-exist"));

        result.Status.ShouldBe(GroupResolutionStatus.Degraded);
    }

    [Fact]
    public async Task Resolver_empty_chain_is_degraded()
    {
        var result = await ResolveAsync(Resolver([Provider("scim-store", () => Groups(SidA))]), Token(), Realm(RealmGroupSource.Resolver));

        result.Status.ShouldBe(GroupResolutionStatus.Degraded);
    }

    [Fact]
    public async Task Inbound_group_claims_are_stripped()
    {
        var inbound = new[]
        {
            IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-21-1-2-3-9999"),
            new Claim(ClaimTypes.GroupSid, "S-1-5-21-1-2-3-9998", ClaimValueTypes.String, "forged"),
            new Claim("groupsid", "S-1-5-21-1-2-3-9997"),
            new Claim("groups", "S-1-5-21-1-2-3-9996")
        };

        var resolverRealm = await ResolveAsync(Resolver([Provider("scim-store", () => Groups(SidA))]), Token(inbound), Realm(RealmGroupSource.Resolver, "scim-store"));
        var tokenRealm = await ResolveAsync(Resolver(), Token([.. inbound, new Claim("groups", SidB)]), Realm(RealmGroupSource.Token));

        Values(resolverRealm).ShouldBe([SidA]);
        Values(tokenRealm).ShouldBe([SidB, "S-1-5-21-1-2-3-9996"]);
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03d")]
    public async Task Group_count_over_cap_is_degraded_and_empty()
    {
        var result = await ResolveAsync(Resolver(maxGroups: 2), Token(new Claim("groups", SidA), new Claim("groups", SidB), new Claim("groups", SidC)), Realm(RealmGroupSource.Token));

        result.Status.ShouldBe(GroupResolutionStatus.Degraded);
        result.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03d")]
    public async Task Rejected_group_log_and_metric_carry_no_value()
    {
        const string secretValue = "very-secret-group-name";
        var logger = new CapturingLogger();
        var tags = new List<(string Key, string? Value)>();
        long total = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "autheris_identity_group_rejected_total") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, t, _) =>
        {
            lock (tags)
            {
                total += value;
                foreach (var tag in t) tags.Add((tag.Key, tag.Value?.ToString()));
            }
        });
        listener.Start();

        await ResolveAsync(Resolver(logger: logger), Token(new Claim("groups", secretValue)), Realm(RealmGroupSource.Token));

        total.ShouldBeGreaterThan(0);
        string[] vocabulary = ["entra", "adfs", "negotiate", "basic", "resolver", "forwardauth"];
        lock (tags)
        {
            tags.ShouldAllBe(t => t.Key == "source" && vocabulary.Contains(t.Value));
        }

        logger.Messages.ShouldNotBeEmpty();
        logger.Messages.ShouldAllBe(m => !m.Contains(secretValue, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-11a")]
    public void Plugin_enrichment_input_is_not_part_of_resolver_contract()
    {
        var parameters = typeof(IGroupSidResolver).GetMethod(nameof(IGroupSidResolver.ResolveAsync))!.GetParameters().Select(p => p.ParameterType).ToArray();

        parameters.ShouldBe([typeof(ClaimsPrincipal), typeof(IdentityRealm), typeof(TenantId), typeof(string), typeof(CancellationToken)]);
    }

    [Fact]
    public async Task Basic_configured_group_sids_are_taken_from_the_trusted_handler()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([IdentityClaimIssuers.CreateGroupSidClaim(SidA), new Claim("groups", SidB)], "Basic"));

        var result = await ResolveAsync(Resolver(), principal, Realm(RealmGroupSource.Token), "Basic");

        Values(result).ShouldBe([SidA]);
    }

    private sealed class CapturingLogger : ILogger<GroupSidResolver>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
