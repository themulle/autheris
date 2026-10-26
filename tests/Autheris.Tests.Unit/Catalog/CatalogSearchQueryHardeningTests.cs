namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Catalog search input bounds: the query length limit is enforced at the engine boundary, the query-embedding cache is a
/// bounded LRU, and the embedding call honours cancellation / a timeout instead of blocking indefinitely.
/// </summary>
public sealed class CatalogSearchQueryHardeningTests
{
    private static IOptions<CatalogSearchOptions> VectorOptions(int maxQueryLength = 256) =>
        Options.Create(new CatalogSearchOptions
        {
            // Any provider other than None enables the vector path for a generator that is not an ICatalogEmbeddingGenerator.
            EmbeddingProvider = CatalogEmbeddingProvider.Ollama,
            MaxQueryLength = maxQueryLength
        });

    private static TableMetadata CreateTable(string domain, string tableName, string description) =>
        new()
        {
            Identifier = new TableIdentifier(domain, "public", tableName),
            Table = new Table { Description = description, Sensitivity = "NORMAL" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };

    private static async Task<CatalogSearchEngine> BuildEngineAsync(ILocalEmbeddingGenerator generator, IOptions<CatalogSearchOptions> options)
    {
        var engine = new CatalogSearchEngine(generator, options, NullLogger<CatalogSearchEngine>.Instance);
        await engine.RebuildIndexAsync([CreateTable("sales", "orders", "customer orders and deliveries")]);
        return engine;
    }

    [Fact]
    public async Task Search_RejectsQueryLongerThanMaxQueryLength_BeforeCallingTheEmbeddingProvider()
    {
        var generator = new CountingEmbeddingGenerator();
        var engine = await BuildEngineAsync(generator, VectorOptions(maxQueryLength: 32));
        generator.Reset();

        var tooLong = new string('a', 33);

        var ex = Should.Throw<GatewayInvalidQueryException>(() => engine.Search(new CatalogSearchQuery(tooLong, Mode: CatalogSearchMode.Hybrid), _ => true));
        ex.ErrorCode.ShouldBe("CATALOG_SEARCH_QUERY_TOO_LONG");
        ex.Message.ShouldNotContain(tooLong);

        await Should.ThrowAsync<GatewayInvalidQueryException>(() => engine.SearchAsync(new CatalogSearchQuery(tooLong, Mode: CatalogSearchMode.Hybrid), _ => true));

        generator.SyncCalls.ShouldBe(0);
        generator.AsyncCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Search_AcceptsQueryAtExactlyMaxQueryLength()
    {
        var engine = await BuildEngineAsync(new CountingEmbeddingGenerator(), VectorOptions(maxQueryLength: 32));

        Should.NotThrow(() => engine.Search(new CatalogSearchQuery("orders" + new string(' ', 26)), _ => true));
    }

    [Fact]
    public void Search_EnforcesDefaultMaxQueryLength_EvenBeforeTheIndexIsReady()
    {
        var engine = new CatalogSearchEngine(new CountingEmbeddingGenerator(), Options.Create(new CatalogSearchOptions()), NullLogger<CatalogSearchEngine>.Instance);

        Should.Throw<GatewayInvalidQueryException>(() => engine.Search(new CatalogSearchQuery(new string('x', 1024 * 1024)), _ => true));
    }

    [Fact]
    public async Task QueryEmbeddingCache_IsBounded_AndReusesEntries()
    {
        var generator = new CountingEmbeddingGenerator();
        var engine = await BuildEngineAsync(generator, VectorOptions());
        generator.Reset();

        engine.Search(new CatalogSearchQuery("customer orders", Mode: CatalogSearchMode.SemanticVector), _ => true);
        engine.Search(new CatalogSearchQuery("customer orders", Mode: CatalogSearchMode.SemanticVector), _ => true);
        generator.SyncCalls.ShouldBe(1);

        for (var i = 0; i < CatalogSearchEngine.QueryEmbeddingCacheCapacity + 200; i++)
        {
            engine.Search(new CatalogSearchQuery($"query {i}", Mode: CatalogSearchMode.SemanticVector), _ => true);
        }

        engine.CachedQueryEmbeddingCount.ShouldBeLessThanOrEqualTo(CatalogSearchEngine.QueryEmbeddingCacheCapacity);
    }

    [Fact]
    public async Task QueryEmbeddingCache_DoesNotPinAFailedEmbedding()
    {
        var generator = new CountingEmbeddingGenerator { ReturnEmpty = true };
        var engine = await BuildEngineAsync(generator, VectorOptions());
        generator.Reset();

        engine.Search(new CatalogSearchQuery("customer orders", Mode: CatalogSearchMode.SemanticVector), _ => true);
        engine.Search(new CatalogSearchQuery("customer orders", Mode: CatalogSearchMode.SemanticVector), _ => true);

        generator.SyncCalls.ShouldBe(2);
        engine.CachedQueryEmbeddingCount.ShouldBe(0);
    }

    [Fact]
    public async Task SearchAsync_UsesTheAsyncEmbeddingPath_AndMatchesSearch()
    {
        var generator = new CountingEmbeddingGenerator();
        var engine = await BuildEngineAsync(generator, VectorOptions());
        generator.Reset();

        var asyncHits = await engine.SearchAsync(new CatalogSearchQuery("customer orders", Mode: CatalogSearchMode.Hybrid), _ => true);

        generator.AsyncCalls.ShouldBe(1);
        generator.SyncCalls.ShouldBe(0);
        asyncHits.ShouldNotBeEmpty();

        // The vector is cached now: the sync path reuses it and returns the same ranking.
        var syncHits = engine.Search(new CatalogSearchQuery("customer orders", Mode: CatalogSearchMode.Hybrid), _ => true);
        generator.SyncCalls.ShouldBe(0);
        syncHits.Count.ShouldBe(asyncHits.Count);
        syncHits[0].TableIdentifier.ShouldBe(asyncHits[0].TableIdentifier);
    }

    [Fact]
    public async Task SearchAsync_PropagatesCancellation()
    {
        var engine = await BuildEngineAsync(new CountingEmbeddingGenerator(), VectorOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(() =>
            engine.SearchAsync(new CatalogSearchQuery("customer orders", Mode: CatalogSearchMode.Hybrid), _ => true, cts.Token));
        ex.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task Ollama_SyncEmbedding_IsBoundedByTheTimeout_InsteadOfBlockingIndefinitely()
    {
        using var httpClient = new HttpClient(new HangingHandler()) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var generator = new OllamaEmbeddingGenerator("http://localhost:11434", "nomic-embed-text", 384, httpClient, null, TimeSpan.FromMilliseconds(200));

        // The handler hangs forever: the call only returns (inside the hang guard) if the 200 ms timeout is enforced.
        var vector = await WaitHelper.RunGuarded(() => generator.GenerateNormalizedEmbedding("customer orders"));

        vector.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task Ollama_AsyncEmbedding_PropagatesCallerCancellation()
    {
        using var httpClient = new HttpClient(new HangingHandler()) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var generator = new OllamaEmbeddingGenerator("http://localhost:11434", "nomic-embed-text", 384, httpClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var ex = await Record.ExceptionAsync(() => generator.GenerateNormalizedEmbeddingsAsync(["a", "b"], cts.Token));
        ex.ShouldBeAssignableTo<OperationCanceledException>();
    }

    /// <summary>Never answers; completes only when the request is cancelled.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private sealed class CountingEmbeddingGenerator : ILocalEmbeddingGenerator
    {
        private int _syncCalls;
        private int _asyncCalls;

        public bool ReturnEmpty { get; init; }
        public int SyncCalls => Volatile.Read(ref _syncCalls);
        public int AsyncCalls => Volatile.Read(ref _asyncCalls);
        public int EmbeddingDimensions => 4;
        public string ModelIdentifier => "counting";

        public void Reset()
        {
            Interlocked.Exchange(ref _syncCalls, 0);
            Interlocked.Exchange(ref _asyncCalls, 0);
        }

        public ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text)
        {
            Interlocked.Increment(ref _syncCalls);
            return Vector();
        }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _asyncCalls);
            var result = new ReadOnlyMemory<float>[texts.Count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = Vector();
            }

            return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(result);
        }

        private ReadOnlyMemory<float> Vector() => ReturnEmpty ? ReadOnlyMemory<float>.Empty : new float[] { 0.5f, 0.5f, 0.5f, 0.5f };
    }
}
