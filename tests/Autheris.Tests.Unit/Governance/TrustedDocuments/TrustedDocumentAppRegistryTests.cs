namespace Autheris.Tests.Unit.Governance.TrustedDocuments;

using Autheris.Application.Governance.TrustedDocuments;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #281 WP2: snapshot registry (SEC-281-7, SEC-281-8).</summary>
[Trait("Category", "Security")]
public sealed class TrustedDocumentAppRegistryTests
{
    private static readonly string H1 = new('a', 64);
    private static readonly string H2 = new('b', 64);

    private readonly ITrustedDocumentRepository _repo = Substitute.For<ITrustedDocumentRepository>();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-23T08:00:00Z"));
    private long _generation = 1;
    private IReadOnlyList<TrustedDocumentVersionRecord> _versions = [new("shop", "1", [H1], ["https://app.example.com"])];

    public TrustedDocumentAppRegistryTests()
    {
        _repo.GetTrustedDocumentGenerationAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(_generation));
        _repo.LoadTrustedDocumentSnapshotAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new TrustedDocumentStoreSnapshot(_generation, _versions)));
    }

    private TrustedDocumentAppRegistry Create() => new(_repo, null, _time);

    private int LoadCalls => _repo.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ITrustedDocumentRepository.LoadTrustedDocumentSnapshotAsync));

    private int GenerationCalls => _repo.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ITrustedDocumentRepository.GetTrustedDocumentGenerationAsync));

    [Fact]
    public async Task FreshLoad_ExposesActiveHashesAndOrigins()
    {
        using var registry = Create();
        var snapshot = await registry.GetAsync(default);

        snapshot.IsDocumentTrusted(new AppIdentity("shop", "1"), H1).ShouldBeTrue();
        snapshot.IsDocumentTrusted(new AppIdentity("shop", "1"), H2).ShouldBeFalse();
        snapshot.IsDocumentTrusted(new AppIdentity("shop", "2"), H1).ShouldBeFalse();
        snapshot.IsDocumentTrusted(new AppIdentity("other", "1"), H1).ShouldBeFalse();
        snapshot.IsOriginAllowed("shop", "https://app.example.com").ShouldBeTrue();
        snapshot.IsOriginAllowed("shop", "https://evil.example.com").ShouldBeFalse();
        snapshot.IsOriginAllowed("other", "https://app.example.com").ShouldBeFalse();
    }

    [Fact]
    public async Task WithinInterval_IsServedFromCache()
    {
        using var registry = Create();
        await registry.GetAsync(default);
        _time.Advance(TimeSpan.FromSeconds(4));
        await registry.GetAsync(default);

        GenerationCalls.ShouldBe(1);
        LoadCalls.ShouldBe(1);
    }

    [Fact]
    public async Task AfterInterval_UnchangedGeneration_ChecksGenerationOnly()
    {
        using var registry = Create();
        await registry.GetAsync(default);
        _time.Advance(TimeSpan.FromSeconds(6));
        await registry.GetAsync(default);

        GenerationCalls.ShouldBe(2);
        LoadCalls.ShouldBe(1);
    }

    [Fact]
    public async Task AfterInterval_ChangedGeneration_ReloadsTheSet()
    {
        using var registry = Create();
        (await registry.GetAsync(default)).IsDocumentTrusted(new AppIdentity("shop", "1"), H2).ShouldBeFalse();

        _generation = 2;
        _versions = [new("shop", "1", [H1, H2], ["https://app.example.com"])];
        _time.Advance(TimeSpan.FromSeconds(6));

        (await registry.GetAsync(default)).IsDocumentTrusted(new AppIdentity("shop", "1"), H2).ShouldBeTrue();
    }

    [Fact]
    public async Task Registry_Retire_EffectiveLocallyImmediately()
    {
        using var registry = Create();
        (await registry.GetAsync(default)).IsDocumentTrusted(new AppIdentity("shop", "1"), H1).ShouldBeTrue();

        _generation = 2;
        _versions = [];   // version retired in the store
        registry.Invalidate();   // no time passes

        (await registry.GetAsync(default)).IsDocumentTrusted(new AppIdentity("shop", "1"), H1).ShouldBeFalse();
    }

    [Fact]
    public async Task Registry_RepositoryThrows_Rejects_And_DoesNotServeStale()
    {
        using var registry = Create();
        await registry.GetAsync(default);   // a good snapshot exists
        _time.Advance(TimeSpan.FromSeconds(6));
        _repo.GetTrustedDocumentGenerationAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<long>(new InvalidOperationException("db down")));

        await Should.ThrowAsync<InvalidOperationException>(async () => await registry.GetAsync(default));
        await Should.ThrowAsync<InvalidOperationException>(async () => await registry.GetAsync(default));   // still no stale snapshot, retried

        _repo.GetTrustedDocumentGenerationAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(_generation));
        (await registry.GetAsync(default)).IsDocumentTrusted(new AppIdentity("shop", "1"), H1).ShouldBeTrue();
    }

    [Fact]
    public async Task Registry_LoadThrows_Rejects_ThenRecovers()
    {
        _repo.LoadTrustedDocumentSnapshotAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<TrustedDocumentStoreSnapshot>(new InvalidOperationException("db down")));
        using var registry = Create();

        await Should.ThrowAsync<InvalidOperationException>(async () => await registry.GetAsync(default));

        _repo.LoadTrustedDocumentSnapshotAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new TrustedDocumentStoreSnapshot(_generation, _versions)));
        (await registry.GetAsync(default)).Generation.ShouldBe(1);
    }

    [Fact]
    public async Task Registry_ConcurrentRefresh_SingleFlight()
    {
        var gate = new TaskCompletionSource();
        _repo.GetTrustedDocumentGenerationAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await gate.Task;
            return _generation;
        });
        using var registry = Create();

        var calls = Enumerable.Range(0, 32).Select(_ => Task.Run(async () => await registry.GetAsync(default))).ToArray();
        await Task.Delay(100);
        gate.SetResult();
        await Task.WhenAll(calls);

        GenerationCalls.ShouldBe(1);
        LoadCalls.ShouldBe(1);
    }
}
