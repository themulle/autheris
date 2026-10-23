namespace Autheris.Tests.Unit.Identity;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>PERS-07: deleting a SCIM group or user invalidates the cached role resolution of every affected user.</summary>
[Trait("Category", "Security")]
public sealed class ScimGroupDeleteCacheInvalidationTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    [Fact]
    public async Task ScimService_DeleteGroup_InvalidatesRoleCacheForMembers()
    {
        var repository = Substitute.For<IIdentityStoreRepository>();
        repository.GetGroupAsync(Tenant, "realm", "g1", Arg.Any<CancellationToken>())
            .Returns(new ScimGroup("g1", "ext", "Group", null, null, null));
        repository.DeleteGroupAsync(Tenant, "realm", "g1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["user-1", "user-2"]));
        var cache = Substitute.For<IRoleResolverCache>();
        var service = new ScimService(repository, Substitute.For<IIdentityDeprovisioningService>(), roleCache: cache);

        var deleted = await service.DeleteGroupAsync(Tenant, "realm", "g1");

        deleted.ShouldBeTrue();
        cache.Received(1).Invalidate("realm", "user-1");
        cache.Received(1).Invalidate("realm", "user-2");
    }

    [Fact]
    public async Task ScimService_DeleteUser_InvalidatesRoleCacheForTheUser()
    {
        var repository = Substitute.For<IIdentityStoreRepository>();
        repository.GetUserAsync(Tenant, "realm", "user-1", Arg.Any<CancellationToken>())
            .Returns(new ScimUser("user-1", "ext", "u@example.com", null, null, true, null, null, null));
        var cache = Substitute.For<IRoleResolverCache>();
        var service = new ScimService(repository, Substitute.For<IIdentityDeprovisioningService>(), roleCache: cache);

        (await service.DeleteUserAsync(Tenant, "realm", "user-1")).ShouldBeTrue();

        cache.Received(1).Invalidate("realm", "user-1");
    }
}
