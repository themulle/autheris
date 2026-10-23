namespace Autheris.Tests.Unit.Identity;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Services;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>GOV-14: a lock broadcast from another replica invalidates the local role cache of the locked subject.</summary>
[Trait("Category", "Security")]
public sealed class LockRoleCacheInvalidationHostedServiceTests
{
    [Fact]
    public async Task LockMessage_InvalidatesLocalRoleCache_GOV14()
    {
        Action<LockPubSubMessage>? handler = null;
        var channel = Substitute.For<IRedisLockPubSubChannel>();
        channel.Subscribe(Arg.Do<Action<LockPubSubMessage>>(h => handler = h)).Returns(Substitute.For<IDisposable>());
        var cache = Substitute.For<IRoleResolverCache>();
        using var sut = new LockRoleCacheInvalidationHostedService(channel, cache);

        await sut.StartAsync(CancellationToken.None);
        handler.ShouldNotBeNull();
        handler!(new LockPubSubMessage("t1", "realm-1", "user-1", "leaver", DateTimeOffset.UtcNow));

        cache.Received(1).Invalidate("realm-1", "user-1");
    }

    [Fact]
    public async Task FailingInvalidation_DropsTheWholeCache_GOV14()
    {
        Action<LockPubSubMessage>? handler = null;
        var channel = Substitute.For<IRedisLockPubSubChannel>();
        channel.Subscribe(Arg.Do<Action<LockPubSubMessage>>(h => handler = h)).Returns(Substitute.For<IDisposable>());
        var cache = Substitute.For<IRoleResolverCache>();
        cache.When(c => c.Invalidate(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new InvalidOperationException());
        using var sut = new LockRoleCacheInvalidationHostedService(channel, cache);

        await sut.StartAsync(CancellationToken.None);
        handler!(new LockPubSubMessage("t1", "realm-1", "user-1", "leaver", DateTimeOffset.UtcNow));

        cache.Received(1).InvalidateAll();
    }
}
