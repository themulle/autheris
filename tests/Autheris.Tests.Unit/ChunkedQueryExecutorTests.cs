using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Xunit;

namespace Autheris.Tests.Unit;

public class ChunkedQueryExecutorTests
{
    private readonly IChunkedQueryExecutor _executor = new ChunkedQueryExecutor(defaultChunkSize: 500);

    [Fact]
    public async Task ExecuteGroupedChunkedQuery_WhenKeysEmpty_DispatchesZeroQueriesAndReturnsEmpty()
    {
        // Arrange
        var keys = Array.Empty<string>();
        int queryCount = 0;

        // Act
        var result = await _executor.ExecuteGroupedChunkedQueryAsync<string, string>(
            keys,
            (chunk, _) =>
            {
                Interlocked.Increment(ref queryCount);
                return Task.FromResult<IReadOnlyDictionary<string, List<string>>>(new Dictionary<string, List<string>>());
            });

        // Assert
        Assert.Empty(result);
        Assert.Equal(0, queryCount);
    }

    [Fact]
    public async Task ExecuteGroupedChunkedQuery_WhenKeysWithinChunkSize_DispatchesSingleQuery()
    {
        // Arrange
        var keys = Enumerable.Range(1, 200).Select(i => $"ID-{i}").ToList();
        int queryCount = 0;

        // Act
        var result = await _executor.ExecuteGroupedChunkedQueryAsync(
            keys,
            (chunk, _) =>
            {
                Interlocked.Increment(ref queryCount);
                var dict = chunk.ToDictionary(k => k, k => new List<string> { $"{k}-Item" });
                return Task.FromResult<IReadOnlyDictionary<string, List<string>>>(dict);
            },
            chunkSize: 500);

        // Assert
        Assert.Equal(200, result.Count);
        Assert.Equal(1, queryCount);
        Assert.Equal("ID-1-Item", result["ID-1"].Single());
    }

    [Fact]
    public async Task ExecuteGroupedChunkedQuery_WhenKeysExceedChunkSize_PartitionsIntoMultipleQueriesAndMergesResults()
    {
        // Arrange: Sonderfall - 1.250 IDs mit Chunk-Limit 500 -> Erfordert genau 3 Queries (500 + 500 + 250)
        var keys = Enumerable.Range(1, 1250).Select(i => $"INV-{i}").ToList();
        var capturedChunks = new List<int>();
        var lockObj = new object();

        // Act
        var (result, dispatchedQueries) = await _executor.ExecuteGroupedWithMetricsAsync(
            keys,
            (chunk, _) =>
            {
                lock (lockObj)
                {
                    capturedChunks.Add(chunk.Count);
                }
                var dict = chunk.ToDictionary(k => k, k => new List<string> { $"{k}-Child1", $"{k}-Child2" });
                return Task.FromResult<IReadOnlyDictionary<string, List<string>>>(dict);
            },
            chunkSize: 500);

        // Assert
        Assert.Equal(3, dispatchedQueries);
        Assert.Equal(1250, result.Count);
        Assert.Contains(500, capturedChunks);
        Assert.Contains(250, capturedChunks);

        // Verify each key has its aggregated children
        for (int i = 1; i <= 1250; i++)
        {
            var key = $"INV-{i}";
            Assert.True(result.ContainsKey(key));
            Assert.Equal(2, result[key].Count);
            Assert.Equal($"{key}-Child1", result[key][0]);
            Assert.Equal($"{key}-Child2", result[key][1]);
        }
    }

    [Fact]
    public async Task ExecuteGroupedChunkedQuery_WhenKeysContainDuplicates_DeduplicatesBeforeChunking()
    {
        // Arrange: 1000 IDs, but only 2 unique IDs -> should be 1 chunk of 2 IDs, not multiple chunks
        var keys = Enumerable.Repeat("KEY-A", 500).Concat(Enumerable.Repeat("KEY-B", 500)).ToList();
        int queryCount = 0;
        int receivedKeyCount = 0;

        // Act
        var result = await _executor.ExecuteGroupedChunkedQueryAsync(
            keys,
            (chunk, _) =>
            {
                Interlocked.Increment(ref queryCount);
                Interlocked.Add(ref receivedKeyCount, chunk.Count);
                var dict = chunk.ToDictionary(k => k, k => new List<string> { $"{k}-Child" });
                return Task.FromResult<IReadOnlyDictionary<string, List<string>>>(dict);
            },
            chunkSize: 500);

        // Assert
        Assert.Equal(1, queryCount);
        Assert.Equal(2, receivedKeyCount);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ExecuteGroupedChunkedQuery_WhenAChunkQueryThrows_PropagatesException()
    {
        // Arrange
        var keys = Enumerable.Range(1, 1500).Select(i => $"KEY-{i}").ToList();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _executor.ExecuteGroupedChunkedQueryAsync(
                keys,
                (chunk, _) =>
                {
                    if (chunk.Contains("KEY-750"))
                    {
                        throw new InvalidOperationException("Simulierter DB Verbindungsabbruch während Chunk 2");
                    }
                    var dict = chunk.ToDictionary(k => k, k => new List<string>());
                    return Task.FromResult<IReadOnlyDictionary<string, List<string>>>(dict);
                },
                chunkSize: 500);
        });
    }

    [Fact]
    public async Task ExecuteChunkedQuery_SingleItemMapping_MergesAllChunksSuccessfully()
    {
        // Arrange
        var keys = Enumerable.Range(1, 1200).Select(i => $"ROW-{i}").ToList();

        // Act
        var (result, queries) = await _executor.ExecuteWithMetricsAsync(
            keys,
            (chunk, _) =>
            {
                var dict = chunk.ToDictionary(k => k, k => k.Length);
                return Task.FromResult<IReadOnlyDictionary<string, int>>(dict);
            },
            chunkSize: 400);

        // Assert: 1200 / 400 = 3 queries
        Assert.Equal(3, queries);
        Assert.Equal(1200, result.Count);
        Assert.Equal(5, result["ROW-1"]);
        Assert.Equal(8, result["ROW-1000"]);
    }

    [Fact]
    public async Task GatewayExecutionService_LoadInvoiceItemsBatchAsync_WhenBatchIsLarge_DispatchesMultipleChunkQueries()
    {
        // Arrange
        var eventBus = new Autheris.Infrastructure.Messaging.InProcessChannelEventBus();
        var epochService = new Autheris.Infrastructure.Cache.EpochValidationService(eventBus: eventBus);
        using var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var cacheService = new Autheris.Infrastructure.Cache.ConsentCacheService(memoryCache, epochService, eventBus);
        var repoOptions = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions
        {
            GovernanceDb = new Autheris.Domain.Options.GovernanceDbOptions
            {
                ConnectionString = $"Data Source=chunked_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            }
        });
        using var repository = new Autheris.Infrastructure.Persistence.SqliteGovernanceRepository(epochService, repoOptions);
        var resolutionService = new ConsentResolutionService();
        var maskingProvider = new ColumnMaskingProvider();

        var options = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions
        {
            GraphQL = new Autheris.Domain.Options.GraphQLOptions
            {
                MaxInClauseBatchSize = 100 // Chunk-Limit 100
            }
        });

        var executor = new ChunkedQueryExecutor(defaultChunkSize: 100);
        var executionService = new Autheris.Application.Services.GatewayExecutionService(
            repository,
            resolutionService,
            cacheService,
            maskingProvider,
            executor,
            options);

        // Grant consent for finance_items
        var childTableId = new Autheris.Domain.Common.TableIdentifier("finance", "dbo", "finance_items");
        var childMeta = await repository.GetTableMetadataAsync(childTableId);
        var consent = new Autheris.Domain.Model.Consent
        {
            TableId = childMeta!.Table.Id,
            TableIdentifier = childTableId,
            Effect = Autheris.Domain.Model.ConsentEffect.Allow,
            GranteeType = Autheris.Domain.Model.GranteeType.User,
            GranteeSid = new Autheris.Domain.Common.Sid("S-1-5-21-TESTUSER-BATCH"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        await repository.CreateConsentAsync(consent);

        var claims = new[]
        {
            new System.Security.Claims.Claim("objectSid", "S-1-5-21-TESTUSER-BATCH"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "FinanceViewer")
        };
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "TestAuth"));

        // 250 IDs bei Chunk-Größe 100 -> Erfordert genau 3 Queries (100 + 100 + 50)
        var invoiceIds = Enumerable.Range(1, 250).Select(i => $"INV-BATCH-{i}").ToList();

        // Act
        var result = await executionService.LoadInvoiceItemsBatchAsync(principal, invoiceIds);

        // Assert
        Assert.Equal(3, executionService.LastDispatchedChildQueryCount);
        Assert.Equal(250, result.Count);
        Assert.Equal(2, result["INV-BATCH-1"].Count);
        Assert.Equal(2, result["INV-BATCH-250"].Count);
        Assert.Equal("INV-BATCH-1-ITEM-1", result["INV-BATCH-1"][0].Id);
    }

    [Fact]
    public async Task ExecuteWithMetricsAsync_RespectsMaxDegreeOfParallelism()
    {
        var keys = Enumerable.Range(1, 10).Select(i => $"KEY-{i}").ToList();
        var executor = new ChunkedQueryExecutor(defaultChunkSize: 1, maxDegreeOfParallelism: 2);

        int currentConcurrency = 0;
        int maxObservedConcurrency = 0;

        var (result, dispatched) = await executor.ExecuteWithMetricsAsync(
            keys,
            async (chunk, ct) =>
            {
                var running = Interlocked.Increment(ref currentConcurrency);
                lock (keys)
                {
                    if (running > maxObservedConcurrency)
                    {
                        maxObservedConcurrency = running;
                    }
                }

                await Task.Delay(20, ct);
                Interlocked.Decrement(ref currentConcurrency);

                return (IReadOnlyDictionary<string, string>)chunk.ToDictionary(k => k, k => $"{k}-val");
            });

        Assert.Equal(10, dispatched);
        Assert.Equal(10, result.Count);
        Assert.True(maxObservedConcurrency <= 2, $"Observed concurrency {maxObservedConcurrency} exceeded limit 2");
    }

    [Fact]
    public async Task ExecuteWithMetricsAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var keys = Enumerable.Range(1, 20).Select(i => $"KEY-{i}").ToList();
        var executor = new ChunkedQueryExecutor(defaultChunkSize: 1, maxDegreeOfParallelism: 2);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await executor.ExecuteWithMetricsAsync(
                keys,
                async (chunk, ct) =>
                {
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(50, ct);
                    return (IReadOnlyDictionary<string, string>)chunk.ToDictionary(k => k, k => "val");
                },
                ct: cts.Token);
        });
    }
}

