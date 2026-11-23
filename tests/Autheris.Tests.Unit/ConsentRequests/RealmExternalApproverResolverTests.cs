namespace Autheris.Tests.Unit.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Tenancy;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class RealmExternalApproverResolverTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    private static ScimUser User(string key, string userName = "alice") =>
        new(key, null, userName, null, null, true, null, null, null);

    private static (IScimUserStore Users, IScimGroupStore Groups) Stores()
    {
        var users = Substitute.For<IScimUserStore>();
        var groups = Substitute.For<IScimGroupStore>();
        groups.GetEffectiveGroupSidsForUserAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EffectiveScimGroupsResult(new HashSet<string>(), false));
        return (users, groups);
    }

    [Fact]
    public async Task Subject_matching_one_user_under_several_key_kinds_is_mapped()
    {
        var (users, groups) = Stores();
        users.GetUserAsync(Tenant, "default", "alice", Arg.Any<CancellationToken>()).Returns(User("u-1"));
        users.GetUserByUserNameAsync(Tenant, "default", "alice", Arg.Any<CancellationToken>()).Returns(User("u-1"));

        var principal = await new RealmExternalApproverResolver(users, groups).ResolveAsync(Tenant, new AssertedApprover("alice"));

        principal.ShouldNotBeNull();
    }

    [Fact]
    public async Task Subject_matching_two_distinct_users_is_unmapped()
    {
        var (users, groups) = Stores();
        // The first key kind finds one user, a later kind finds a different one: ambiguous, so nobody.
        users.GetUserAsync(Tenant, "default", "alice", Arg.Any<CancellationToken>()).Returns(User("u-1"));
        users.GetUserByUserNameAsync(Tenant, "default", "alice", Arg.Any<CancellationToken>()).Returns(User("u-2"));

        var principal = await new RealmExternalApproverResolver(users, groups).ResolveAsync(Tenant, new AssertedApprover("alice"));

        principal.ShouldBeNull();
    }
}
