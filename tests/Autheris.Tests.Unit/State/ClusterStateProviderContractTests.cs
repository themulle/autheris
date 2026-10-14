namespace Autheris.Tests.Unit.State;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.State;
using Shouldly;
using Xunit;

/// <summary>
/// Behavioural contract every <see cref="IDistributedClusterStateProvider"/> must satisfy. A Redis/Garnet variant
/// derives from this class in the integration project.
/// </summary>
public abstract class ClusterStateProviderContractTests
{
    protected abstract IDistributedClusterStateProvider CreateProvider();

    [Fact]
    public async Task Get_WhenKeyAbsent_ReturnsDefault_AndDoesNotThrow()
    {
        var provider = CreateProvider();

        var value = await provider.GetAsync<string>("contract:absent");

        value.ShouldBeNull();
    }

    [Fact]
    public async Task SetThenGet_RoundTrips()
    {
        var provider = CreateProvider();

        await provider.SetAsync("contract:k", "v", TimeSpan.FromMinutes(1));

        (await provider.GetAsync<string>("contract:k")).ShouldBe("v");
    }

    [Fact]
    public async Task GetOrUnavailable_Distinguishes_Found_And_Absent()
    {
        var provider = CreateProvider();
        await provider.SetAsync("contract:present", "x", TimeSpan.FromMinutes(1));

        var found = await provider.GetOrUnavailableAsync<string>("contract:present");
        var absent = await provider.GetOrUnavailableAsync<string>("contract:missing");

        found.Found.ShouldBeTrue();
        found.Value.ShouldBe("x");
        found.Unavailable.ShouldBeFalse();
        absent.Found.ShouldBeFalse();
        absent.Unavailable.ShouldBeFalse();
    }

    [Fact]
    public async Task TryAcquireLock_WhenBusy_ReturnsNull_NotThrows()
    {
        var provider = CreateProvider();

        await using var first = await provider.TryAcquireLockAsync("contract:lock", TimeSpan.FromSeconds(30));
        var second = await provider.TryAcquireLockAsync("contract:lock", TimeSpan.FromSeconds(30));

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task Increment_ConcurrentDeltas_SumExactly()
    {
        var provider = CreateProvider();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ =>
            Task.Run(async () => await provider.IncrementAsync("contract:ctr", 3, TimeSpan.FromMinutes(1)))));

        var total = await provider.IncrementAsync("contract:ctr", 0, TimeSpan.FromMinutes(1));
        total.ShouldBe(600L);
    }

    [Fact]
    public async Task Increment_ConcurrentFirstWriters_ExactlyOneObservesOne()
    {
        var provider = CreateProvider();

        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ =>
            Task.Run(async () => await provider.IncrementAsync("contract:nonce", 1, TimeSpan.FromMinutes(1)))));

        results.Count(r => r == 1).ShouldBe(1);
        results.Max().ShouldBe(64L);
    }

    [Fact]
    public async Task Publish_WithThrowingSubscriber_StillDeliversToOthers_AndDoesNotThrow()
    {
        var provider = CreateProvider();
        var delivered = 0;
        await using var bad = provider.SubscribeAsync<string>("contract:chan", _ => throw new InvalidOperationException("boom"));
        await using var good = provider.SubscribeAsync<string>("contract:chan", _ =>
        {
            delivered++;
            return ValueTask.CompletedTask;
        });

        await provider.PublishEventAsync("contract:chan", "payload");

        // Redis delivers asynchronously; in-memory delivers inline. Wait briefly for either.
        for (var i = 0; i < 50 && delivered == 0; i++)
        {
            await Task.Delay(20);
        }

        delivered.ShouldBe(1);
    }
}

public sealed class InMemoryClusterStateProviderContractTests : ClusterStateProviderContractTests
{
    protected override IDistributedClusterStateProvider CreateProvider() => new InMemoryClusterStateProvider();
}
