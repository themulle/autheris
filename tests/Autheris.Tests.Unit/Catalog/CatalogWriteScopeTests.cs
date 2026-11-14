namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Tests.Common;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

/// <summary>
/// Test double for a catalog repository: it follows the same protocol as the real repository (per-table epoch always,
/// version bump and event deferred to an active scope only for additive writes).
/// </summary>
internal sealed class FakeCatalogRepository : ICatalogVersionFlusher
{
    private readonly object _gate = new();
    private int _epochs;

    public List<(long Structure, long Content, CancellationToken Token)> Flushes { get; } = [];

    public Func<long, long, CancellationToken, Task>? OnFlush { get; set; }

    public int EpochIncrements => Volatile.Read(ref _epochs);

    public int FlushCount
    {
        get
        {
            lock (_gate)
            {
                return Flushes.Count;
            }
        }
    }

    public async Task FlushCatalogVersionAsync(long structureDelta, long contentDelta, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Flushes.Add((structureDelta, contentDelta, ct));
        }

        if (OnFlush is { } onFlush)
        {
            await onFlush(structureDelta, contentDelta, ct).ConfigureAwait(false);
        }
    }

    public async Task AdditiveWriteAsync()
    {
        Interlocked.Increment(ref _epochs);
        var scope = CatalogWriteScopeContext.TryGetActive(this);
        if (scope is null)
        {
            await FlushCatalogVersionAsync(1, 0).ConfigureAwait(false);
            return;
        }

        await scope.RecordAdditiveWriteAsync(1, 0).ConfigureAwait(false);
    }

    public async Task RestrictiveWriteAsync()
    {
        Interlocked.Increment(ref _epochs);
        await FlushCatalogVersionAsync(1, 0).ConfigureAwait(false);
        var scope = CatalogWriteScopeContext.TryGetActive(this);
        if (scope is not null)
        {
            await scope.FlushPendingAsync().ConfigureAwait(false);
        }
    }
}

public sealed class CatalogWriteScopeTests
{
    private static (FakeCatalogRepository Repo, CatalogDirtyFlag Dirty, FakeTimeProvider Time, CatalogWriteScopeFactory Factory) Create()
    {
        var repo = new FakeCatalogRepository();
        var dirty = new CatalogDirtyFlag();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        return (repo, dirty, time, new CatalogWriteScopeFactory(repo, dirty, timeProvider: time));
    }

    [Fact]
    public async Task Upserts_InScope_PublishOneCatalogChangedOnDispose()
    {
        var (repo, _, _, factory) = Create();

        await using (factory.Begin("test"))
        {
            for (var i = 0; i < 10; i++)
            {
                await repo.AdditiveWriteAsync();
            }

            repo.FlushCount.ShouldBe(0);
        }

        repo.FlushCount.ShouldBe(1);
        repo.Flushes[0].Structure.ShouldBe(1);
        repo.Flushes[0].Content.ShouldBe(0);
    }

    [Fact]
    public async Task BumpVersionOncePerChunk()
    {
        var (repo, _, _, factory) = Create();

        await using (factory.Begin("test"))
        {
            for (var i = 0; i < 1200; i++)
            {
                await repo.AdditiveWriteAsync();
            }

            // Two full chunks of CatalogWriteScopeSettings.ChunkSize (500) were flushed while the scope was still open.
            repo.FlushCount.ShouldBe(2);
        }

        repo.FlushCount.ShouldBe(3);
    }

    [Fact]
    public async Task StillIncrementEpochPerChangedTable()
    {
        var (repo, _, _, factory) = Create();

        await using (factory.Begin("test"))
        {
            for (var i = 0; i < 25; i++)
            {
                await repo.AdditiveWriteAsync();
            }

            // INV-1: every changed table keeps its own epoch; only the version bump and event are deferred.
            repo.EpochIncrements.ShouldBe(25);
            repo.FlushCount.ShouldBe(0);
        }
    }

    [Fact]
    public async Task FlushOnException_PublishesForCommittedTables()
    {
        var (repo, _, _, factory) = Create();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await using var scope = factory.Begin("test");
            await repo.AdditiveWriteAsync();
            await repo.AdditiveWriteAsync();
            await repo.AdditiveWriteAsync();
            throw new InvalidOperationException("writer failed");
        });

        ex.Message.ShouldBe("writer failed");
        repo.FlushCount.ShouldBe(1);
    }

    [Fact]
    public async Task FlushOnCancellation_Uses30sTimeoutToken()
    {
        var (repo, dirty, time, factory) = Create();
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repo.OnFlush = async (_, _, ct) =>
        {
            flushStarted.TrySetResult();
            await new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(ct);
        };

        var scope = factory.Begin("test");
        await repo.AdditiveWriteAsync();

        var dispose = scope.DisposeAsync().AsTask();
        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var token = repo.Flushes[0].Token;
        token.CanBeCanceled.ShouldBeTrue();
        token.ShouldNotBe(CancellationToken.None);
        token.IsCancellationRequested.ShouldBeFalse();

        time.Advance(CatalogWriteScopeSettings.FlushTimeout - TimeSpan.FromSeconds(1));
        token.IsCancellationRequested.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));

        token.IsCancellationRequested.ShouldBeTrue();
        dirty.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task NestedScopes_JoinOuter()
    {
        var (repo, _, _, factory) = Create();

        await using (var outer = factory.Begin("outer"))
        {
            await using (var inner = factory.Begin("inner"))
            {
                inner.ScopeId.ShouldBe(outer.ScopeId);
                await repo.AdditiveWriteAsync();
            }

            // The inner scope ended without flushing: only the outermost scope flushes.
            repo.FlushCount.ShouldBe(0);
            await repo.AdditiveWriteAsync();
        }

        repo.FlushCount.ShouldBe(1);
    }

    [Fact]
    public async Task NoScope_PublishesPerWrite()
    {
        var (repo, _, _, _) = Create();

        await repo.AdditiveWriteAsync();
        await repo.AdditiveWriteAsync();
        await repo.AdditiveWriteAsync();

        repo.FlushCount.ShouldBe(3);
    }
}
