using System.Security.Claims;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Providers;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class LdapsRoleProviderTests
{
    private const string UserSid = "S-1-5-21-1-2-3-1001";
    private static readonly IdentityRealm Realm = new(
        "r", IdentityRealmKind.Kerberos, TenantBindingSpecification.Fixed(new TenantId("t")), RealmGroupSource.Resolver, RealmProvisioningMode.Scim, resolverChain: [LdapsRoleProvider.Id]);

    private static ResolvedIdentity Identity(string? upn = null, params Claim[] claims) =>
        new("user-key", upn, "alice", "alice@corp.example", new TenantId("t"), "r", [.. claims]);

    private static byte[] Binary(string sid)
    {
        SidFormat.TryToBinary(sid, out var bytes).ShouldBeTrue();
        return bytes;
    }

    private static ILdapDirectoryAdapter Adapter(IReadOnlyList<string>? users = null, IReadOnlyList<byte[]>? groups = null)
    {
        var adapter = Substitute.For<ILdapDirectoryAdapter>();
        adapter.FindUserDnsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(users ?? ["CN=alice,DC=corp"]));
        adapter.ReadTokenGroupsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(groups ?? []));
        return adapter;
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03g")]
    public async Task TokenGroups_malformed_entry_degrades()
    {
        var adapter = Adapter(groups: [Binary("S-1-5-21-1-2-3-1105"), [1, 2, 3]]);
        var sut = new LdapsRoleProvider(adapter);

        var result = await sut.GetRolesAsync(Identity(claims: new Claim(ClaimTypes.PrimarySid, UserSid)), Realm);

        result.Failed.ShouldBeTrue();
        result.Groups.ShouldBe(["S-1-5-21-1-2-3-1105"]);
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03g")]
    public async Task User_lookup_by_sid_not_by_name_claim()
    {
        var adapter = Adapter();
        var sut = new LdapsRoleProvider(adapter);

        await sut.GetRolesAsync(Identity(claims: [new Claim(ClaimTypes.PrimarySid, UserSid), new Claim("name", "alice"), new Claim("sAMAccountName", "alice")]), Realm);

        var filter = (string)adapter.ReceivedCalls().First(c => c.GetMethodInfo().Name == nameof(ILdapDirectoryAdapter.FindUserDnsAsync)).GetArguments()[0]!;
        filter.ShouldContain("(objectSid=");
        filter.ShouldNotContain("alice");
        filter.ShouldNotContain("sAMAccountName");
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03g")]
    public async Task Lookup_returning_two_objects_is_degraded()
    {
        var sut = new LdapsRoleProvider(Adapter(users: ["CN=a,DC=corp", "CN=b,DC=corp"]));
        var none = new LdapsRoleProvider(Adapter(users: []));
        var identity = Identity(claims: new Claim(ClaimTypes.PrimarySid, UserSid));

        (await sut.GetRolesAsync(identity, Realm)).Failed.ShouldBeTrue();
        (await none.GetRolesAsync(identity, Realm)).Failed.ShouldBeTrue();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03g")]
    public async Task Ldaps_timeout_is_degraded_and_trips_breaker()
    {
        var adapter = Substitute.For<ILdapDirectoryAdapter>();
        adapter.FindUserDnsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<string>>>(async call =>
            {
                await new TaskCompletionSource<bool>().Task.WaitAsync(call.Arg<CancellationToken>());
                return [];
            });
        var sut = new LdapsRoleProvider(adapter, timeout: TimeSpan.FromMilliseconds(50), failureThreshold: 2);
        var identity = Identity(claims: new Claim(ClaimTypes.PrimarySid, UserSid));

        (await sut.GetRolesAsync(identity, Realm)).Failed.ShouldBeTrue();
        (await sut.GetRolesAsync(identity, Realm)).Failed.ShouldBeTrue();
        sut.IsCircuitBreakerOpen.ShouldBeTrue();
        adapter.ClearReceivedCalls();

        (await sut.GetRolesAsync(identity, Realm)).Failed.ShouldBeTrue();
        await adapter.DidNotReceiveWithAnyArgs().FindUserDnsAsync(default!, default, default);
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03g")]
    public void Ldap_filter_value_is_escaped_for_binary_sid()
    {
        LdapsRoleProvider.EscapeBinary(Binary("S-1-5-21-1-2-3-1105")).ShouldBe(@"\01\05\00\00\00\00\00\05\15\00\00\00\01\00\00\00\02\00\00\00\03\00\00\00\51\04\00\00");
        LdapsRoleProvider.SanitizeLdapFilter("a*(b)\\\u0001é\ud800").ShouldBe(@"a\2a\28b\29\5c\01\c3\a9\ef\bf\bd");
    }
}
