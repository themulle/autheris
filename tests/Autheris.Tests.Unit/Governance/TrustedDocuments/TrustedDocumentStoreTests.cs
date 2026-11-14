namespace Autheris.Tests.Unit.Governance.TrustedDocuments;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Governance.TrustedDocuments;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class TrustedDocumentStoreTests
{
    private sealed class InMemoryTrustedDocRepo : ITrustedDocumentRepository
    {
        private long _generation = 1;
        private IReadOnlyList<TrustedDocumentVersionRecord> _versions;

        public InMemoryTrustedDocRepo(IReadOnlyList<TrustedDocumentVersionRecord> initial)
        {
            _versions = initial;
        }

        public void SetActive(IReadOnlyList<TrustedDocumentVersionRecord> versions)
        {
            _generation++;
            _versions = versions;
        }

        public Task<long> GetTrustedDocumentGenerationAsync(CancellationToken ct = default)
            => Task.FromResult(_generation);

        public Task<TrustedDocumentStoreSnapshot> LoadTrustedDocumentSnapshotAsync(CancellationToken ct = default)
            => Task.FromResult(new TrustedDocumentStoreSnapshot(_generation, _versions));

        public Task<TrustedDocumentAppView?> GetTrustedDocumentAppAsync(string appId, CancellationToken ct = default)
            => Task.FromResult<TrustedDocumentAppView?>(null);

        public Task<TrustedDocumentSetDelta> ReplaceTrustedDocumentVersionAsync(
            string appId,
            string version,
            IReadOnlyList<string> allowedOrigins,
            IReadOnlyList<string> documentHashes,
            int maxActiveVersionsPerApp,
            DateTimeOffset now,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TrustedDocumentRetireOutcome> RetireTrustedDocumentVersionAsync(
            string appId,
            string version,
            DateTimeOffset now,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public void Same_bytes_hit_hash_cache_without_parse()
    {
        var cache = new TrustedDocumentNormalizedHashCache();
        const string doc = "query   HelloQuery { hello }";
        var store = new TrustedDocumentStore([], cache);

        var bytes = Encoding.UTF8.GetBytes(doc);
        // Prime with an allowed document in a second store sharing the same hash cache.
        var allowingStore = new TrustedDocumentStore(["query HelloQuery { hello }"], cache);
        var parsesBefore = cache.ParseCount;
        parsesBefore.ShouldBe(1);

        // Equivalent formatted bytes hit cache on repeated lookups (only one parse for the new exact byte sequence).
        allowingStore.IsTrusted(bytes).ShouldBeTrue();
        cache.ParseCount.ShouldBe(parsesBefore + 1);

        allowingStore.IsTrusted(bytes).ShouldBeTrue();
        allowingStore.IsTrusted(bytes).ShouldBeTrue();
        allowingStore.IsTrusted(doc).ShouldBeTrue();
        store.IsTrusted(bytes).ShouldBeFalse();
        cache.ParseCount.ShouldBe(parsesBefore + 1);
    }

    [Fact]
    public void Hash_cache_respects_entry_and_byte_bounds()
    {
        var cache = new TrustedDocumentNormalizedHashCache();
        for (var i = 0; i < 1_100; i++)
        {
            var bytes = Encoding.UTF8.GetBytes($"query Q{i} {{ field{i} }}");
            cache.GetOrComputeNormalizedHash(bytes);
        }

        cache.Count.ShouldBeLessThanOrEqualTo(TrustedDocumentNormalizedHashCache.MaxEntries);
        cache.TotalBytes.ShouldBeLessThanOrEqualTo(TrustedDocumentNormalizedHashCache.MaxTotalBytes);

        // Large payload (> 2 MiB) forces total byte bound eviction without exceeding 4 MiB.
        var bigPad = new string(' ', 2 * 1024 * 1024);
        for (var i = 0; i < 3; i++)
        {
            var bigDoc = Encoding.UTF8.GetBytes($"query Big{i} {{ hello }}{bigPad}");
            cache.GetOrComputeNormalizedHash(bigDoc);
        }

        cache.TotalBytes.ShouldBeLessThanOrEqualTo(TrustedDocumentNormalizedHashCache.MaxTotalBytes);
    }

    [Fact]
    public async Task Registry_change_applies_to_cached_hash()
    {
        const string doc = "query LiveCheck { hello }";
        const string origin = "https://app.example.com";

        // 1. Global TrustedDocumentStore caches hash without caching trust decision across stores.
        var cache = new TrustedDocumentNormalizedHashCache();
        var trustedStore = new TrustedDocumentStore([doc], cache);
        var revokedStore = new TrustedDocumentStore([], cache);
        var docBytes = Encoding.UTF8.GetBytes(doc);

        trustedStore.IsTrusted(docBytes).ShouldBeTrue();
        var parsesAfterPrime = cache.ParseCount;
        revokedStore.IsTrusted(docBytes).ShouldBeFalse();
        cache.ParseCount.ShouldBe(parsesAfterPrime);

        // 2. App-scoped registry invalidation takes effect immediately on the next middleware request.
        var repo = new InMemoryTrustedDocRepo([new TrustedDocumentVersionRecord("shop", "1", [TrustedDocumentHash.ComputeExact(doc)], [origin])]);
        var registry = new TrustedDocumentAppRegistry(repo, null, TimeProvider.System);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new GatewayOptions
        {
            GraphQL = new GraphQLOptions { PersistedQueriesOnly = true, AppScopedTrustedDocuments = new AppScopedTrustedDocumentsOptions { Enabled = true } }
        }));
        services.AddSingleton<ITrustedDocumentAppRegistry>(registry);
        services.AddSingleton(trustedStore);
        services.AddGraphQLServer()
            .AddQueryType(d => d.Name("Query").Field("hello").Type<HotChocolate.Types.StringType>().Resolve(_ => new ValueTask<object?>("world")))
            .UseExceptions()
            .UseTimeout()
            .UseDocumentCache()
            .UseRequest<TrustedDocumentsOnlyMiddleware>()
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseOperationCache()
            .UseOperationCompiler()
            .UseOperationVariableCoercion()
            .UseOperationExecution();

        await using var sp = services.BuildServiceProvider();
        var executor = await sp.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync();

        static DefaultHttpContext AppHttp(string header, string orig)
        {
            var http = new DefaultHttpContext();
            http.Request.Headers["X-Autheris-App"] = header;
            http.Request.Headers.Origin = orig;
            return http;
        }

        var first = (OperationResult)await executor.ExecuteAsync(
            OperationRequestBuilder.New().SetDocument(doc).SetGlobalState("HttpContext", AppHttp("shop/1", origin)).Build());
        (first.Errors is null or { Count: 0 }).ShouldBeTrue();

        // Remove document from active registry and invalidate immediately.
        repo.SetActive([]);
        registry.Invalidate();

        var second = (OperationResult)await executor.ExecuteAsync(
            OperationRequestBuilder.New().SetDocument(doc).SetGlobalState("HttpContext", AppHttp("shop/1", origin)).Build());
        second.Errors.ShouldNotBeNull();
        second.Errors.Count.ShouldBe(1);
        second.Errors[0].Code.ShouldBe(TrustedDocumentsOnlyMiddleware.RejectionCode);
    }
}
