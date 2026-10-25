namespace Autheris.Tests.Unit.Security;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Interfaces;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DbCatalogVersionProviderRaceTests
{
    [Fact]
    public async Task SEC_CAT138_F1_InvalidateDuringInFlightLoad_DoesNotCacheStaleValue()
    {
        var gate = new TaskCompletionSource<CatalogVersion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(
                _ =>
                {
                    started.TrySetResult();
                    return gate.Task;
                },
                _ => Task.FromResult(new CatalogVersion(2, 1)));
        var provider = new DbCatalogVersionProvider(repo, ttl: TimeSpan.FromSeconds(60), timeProvider: new FakeTimeProvider());

        var inFlight = provider.GetCatalogVersionAsync().AsTask();
        await started.Task;
        provider.Invalidate();
        gate.SetResult(new CatalogVersion(1, 1));

        (await inFlight).StructureVersion.ShouldBe(1);
        var next = await provider.GetCatalogVersionAsync();
        next.StructureVersion.ShouldBe(2);
        await repo.Received(2).GetCatalogVersionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SEC_CAT138_F1_CatalogChangedHandlerDuringInFlightLoad_DoesNotCacheStaleValue()
    {
        Func<object, Task>? handler = null;
        var bus = Substitute.For<IEventBus>();
        bus.Subscribe<object>(ICatalogVersionProvider.CatalogChangedEventChannel, Arg.Do<Func<object, Task>>(h => handler = h))
            .Returns(Substitute.For<IDisposable>());
        var gate = new TaskCompletionSource<CatalogVersion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(
                _ =>
                {
                    started.TrySetResult();
                    return gate.Task;
                },
                _ => Task.FromResult(new CatalogVersion(2, 1)));
        var provider = new DbCatalogVersionProvider(repo, bus, TimeSpan.FromSeconds(60), new FakeTimeProvider());

        var inFlight = provider.GetCatalogVersionAsync().AsTask();
        await started.Task;
        handler.ShouldNotBeNull();
        await handler(new object());
        gate.SetResult(new CatalogVersion(1, 1));
        await inFlight;

        (await provider.GetCatalogVersionAsync()).StructureVersion.ShouldBe(2);
    }

    [Fact]
    public async Task Ttl_WithinWindow_HitsCache_AfterWindow_Reloads()
    {
        var time = new FakeTimeProvider();
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new CatalogVersion(1, 1), new CatalogVersion(2, 1));
        var provider = new DbCatalogVersionProvider(repo, ttl: TimeSpan.FromSeconds(5), timeProvider: time);

        (await provider.GetCatalogVersionAsync()).StructureVersion.ShouldBe(1);
        time.Advance(TimeSpan.FromSeconds(4));
        (await provider.GetCatalogVersionAsync()).StructureVersion.ShouldBe(1);
        await repo.Received(1).GetCatalogVersionAsync(Arg.Any<CancellationToken>());

        time.Advance(TimeSpan.FromSeconds(2));
        (await provider.GetCatalogVersionAsync()).StructureVersion.ShouldBe(2);
        await repo.Received(2).GetCatalogVersionAsync(Arg.Any<CancellationToken>());
    }
}
