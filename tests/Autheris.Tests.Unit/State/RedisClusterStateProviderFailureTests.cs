#pragma warning disable CS0618 // StackExchange.Redis exception constructors without CommandFlags are fine for test doubles
namespace Autheris.Tests.Unit.State;

using System;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Domain.Options;
using Autheris.Infrastructure.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using StackExchange.Redis;
using Xunit;

[Trait("Category", "Security")]
public class RedisClusterStateProviderFailureTests
{
    private static RedisClusterStateProvider CreateFailing(Exception failure)
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Throws(failure);
        multiplexer.GetSubscriber(Arg.Any<object>()).Throws(failure);
        return new RedisClusterStateProvider(
            multiplexer,
            Options.Create(new GatewayOptions()),
            NullLogger<RedisClusterStateProvider>.Instance);
    }

    private static RedisConnectionException ConnectionFailure() =>
        new(ConnectionFailureType.UnableToConnect, "redis down");

    [Fact]
    public async Task Get_WhenRedisThrows_ThrowsClusterStateUnavailable_NotDefault()
    {
        var provider = CreateFailing(ConnectionFailure());

        var ex = await Should.ThrowAsync<ClusterStateUnavailableException>(async () => await provider.GetAsync<string>("k"));

        ex.InnerException.ShouldBeOfType<RedisConnectionException>();
    }

    [Fact]
    public async Task GetOrUnavailable_WhenRedisThrows_ReportsUnavailable()
    {
        var provider = CreateFailing(ConnectionFailure());

        var read = await provider.GetOrUnavailableAsync<string>("k");

        read.Unavailable.ShouldBeTrue();
        read.Found.ShouldBeFalse();
    }

    [Fact]
    public async Task Set_WhenRedisThrows_Throws()
    {
        var provider = CreateFailing(ConnectionFailure());

        await Should.ThrowAsync<ClusterStateUnavailableException>(async () => await provider.SetAsync("k", "v", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Remove_WhenRedisThrows_Throws()
    {
        var provider = CreateFailing(ConnectionFailure());

        await Should.ThrowAsync<ClusterStateUnavailableException>(async () => await provider.RemoveAsync("k"));
    }

    [Fact]
    public async Task TryAcquireLock_WhenRedisThrows_Throws_NotNull()
    {
        var provider = CreateFailing(ConnectionFailure());

        await Should.ThrowAsync<ClusterStateUnavailableException>(async () =>
            await provider.TryAcquireLockAsync("res", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Publish_WhenRedisThrows_Throws()
    {
        var provider = CreateFailing(ConnectionFailure());

        await Should.ThrowAsync<ClusterStateUnavailableException>(async () => await provider.PublishEventAsync("chan", "x"));
    }

    [Fact]
    public async Task Increment_WhenRedisThrows_ReturnsNull_MeaningUnavailable()
    {
        var provider = CreateFailing(ConnectionFailure());

        (await provider.IncrementAsync("k", 1, TimeSpan.FromMinutes(1))).ShouldBeNull();
    }

    [Fact]
    public async Task TryConsumeBudget_WhenRedisThrows_ReportsStoreUnavailable()
    {
        var provider = CreateFailing(ConnectionFailure());

        var (outcome, _) = await provider.TryConsumeBudgetAsync("k", 1, 10, TimeSpan.FromMinutes(1));

        outcome.ShouldBe(BudgetConsumeOutcome.StoreUnavailable);
    }

    [Fact]
    public async Task Get_WhenNonInfrastructureBugThrows_IsNotMaskedAsUnavailable()
    {
        var provider = CreateFailing(new InvalidOperationException("programming error"));

        await Should.ThrowAsync<InvalidOperationException>(async () => await provider.GetAsync<string>("k"));
    }
}
