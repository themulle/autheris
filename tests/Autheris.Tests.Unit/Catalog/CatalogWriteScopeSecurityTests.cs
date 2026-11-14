namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogWriteScopeSecurityTests
{
    private static (FakeCatalogRepository Repo, CatalogDirtyFlag Dirty, FakeTimeProvider Time, CatalogWriteScopeFactory Factory) Create()
    {
        var repo = new FakeCatalogRepository();
        var dirty = new CatalogDirtyFlag();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        return (repo, dirty, time, new CatalogWriteScopeFactory(repo, dirty, timeProvider: time));
    }

    [Fact]
    public async Task HiddenFlagSet_InScope_FlushesImmediately()
    {
        var (repo, _, _, factory) = Create();

        await using (factory.Begin("test"))
        {
            await repo.AdditiveWriteAsync();
            await repo.AdditiveWriteAsync();
            repo.FlushCount.ShouldBe(0);

            // Restrictive write (hidden flag): the version is bumped and published before the writer continues.
            await repo.RestrictiveWriteAsync();

            // The restrictive bump and the pending additive delta were both flushed; nothing is left for the end of the scope.
            repo.FlushCount.ShouldBe(2);
        }

        repo.FlushCount.ShouldBe(2);
    }

    [Fact]
    public async Task MaskingRuleAdded_InScope_FlushesImmediately()
    {
        var (repo, _, _, factory) = Create();

        await using (factory.Begin("test"))
        {
            await repo.RestrictiveWriteAsync();
            repo.FlushCount.ShouldBe(1);

            await repo.AdditiveWriteAsync();
            repo.FlushCount.ShouldBe(1);
        }

        repo.FlushCount.ShouldBe(2);
    }

    [Fact]
    public async Task WriteAfterScopeDisposed_PublishesImmediately()
    {
        var (repo, _, _, factory) = Create();

        var scope = factory.Begin("test");
        await repo.AdditiveWriteAsync();
        await scope.DisposeAsync();
        repo.FlushCount.ShouldBe(1);

        await repo.AdditiveWriteAsync();

        repo.FlushCount.ShouldBe(2);
        CatalogWriteScopeContext.TryGetActive(repo).ShouldBeNull();
    }

    [Fact]
    public async Task WriteInTaskRunChild_AfterDispose_IsNotDeferred()
    {
        var (repo, _, _, factory) = Create();

        var scope = factory.Begin("test");
        await scope.DisposeAsync();

        // The ambient slot flows into the child task, but the disposed scope never defers.
        await Task.Run(async () =>
        {
            CatalogWriteScopeContext.TryGetActive(repo).ShouldBeNull();
            await repo.AdditiveWriteAsync();
        });

        repo.FlushCount.ShouldBe(1);
    }

    [Fact]
    public async Task ScopeOfAnotherRepository_IsNotActive()
    {
        var (repo, _, _, factory) = Create();
        var foreign = new FakeCatalogRepository();

        await using (factory.Begin("test"))
        {
            CatalogWriteScopeContext.TryGetActive(repo).ShouldNotBeNull();
            CatalogWriteScopeContext.TryGetActive(foreign).ShouldBeNull();

            await foreign.AdditiveWriteAsync();
            foreign.FlushCount.ShouldBe(1);
        }
    }

    [Fact]
    public async Task FlushFailure_SetsDirtyFlag_PollRepairs()
    {
        var (repo, dirty, _, factory) = Create();
        repo.OnFlush = (_, _, _) => throw new InvalidOperationException("db down: password=secret");

        await using (factory.Begin("test"))
        {
            await repo.AdditiveWriteAsync();
        }

        repo.FlushCount.ShouldBe(CatalogWriteScopeSettings.MaxFlushAttempts);
        dirty.IsDirty.ShouldBeTrue();

        // The 30 s snapshot poll forces one version bump and clears the flag.
        repo.OnFlush = null;
        var service = new CatalogSnapshotBackgroundService(
            new NoopSnapshotProvider(),
            NullLogger<CatalogSnapshotBackgroundService>.Instance,
            dirtyFlag: dirty,
            flusher: repo);

        await service.PollOnceAsync(CancellationToken.None);

        dirty.IsDirty.ShouldBeFalse();
        repo.Flushes[^1].Structure.ShouldBe(1);
        repo.Flushes[^1].Content.ShouldBe(1);
        repo.FlushCount.ShouldBe(CatalogWriteScopeSettings.MaxFlushAttempts + 1);
    }

    [Fact]
    public async Task FlushTimeout_DoesNotHangDispose()
    {
        var (repo, dirty, time, factory) = Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repo.OnFlush = async (_, _, ct) =>
        {
            started.TrySetResult();
            await new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(ct);
        };

        var scope = factory.Begin("test");
        await repo.AdditiveWriteAsync();

        var dispose = scope.DisposeAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispose.IsCompleted.ShouldBeFalse();

        time.Advance(CatalogWriteScopeSettings.FlushTimeout);

        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        dirty.IsDirty.ShouldBeTrue();
        repo.FlushCount.ShouldBe(1);
    }

    private sealed class NoopSnapshotProvider : ICatalogSnapshotProvider
    {
        public CatalogSnapshot CurrentSnapshot => null!;

        public ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default) => new(CurrentSnapshot);

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
