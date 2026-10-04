using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Messaging;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class OddBatchDataLoaderEvaluationTests
{
    private readonly ChunkedQueryExecutor _executor = new(defaultChunkSize: 500);

    #region Odd Quantities and Boundary Partitioning in ChunkedQueryExecutor

    [Theory]
    [InlineData(1, 500, 1, new[] { 1 })]
    [InlineData(3, 2, 2, new[] { 2, 1 })]
    [InlineData(7, 3, 3, new[] { 3, 3, 1 })]
    [InlineData(37, 10, 4, new[] { 10, 10, 10, 7 })]
    [InlineData(499, 500, 1, new[] { 499 })]
    [InlineData(500, 500, 1, new[] { 500 })]
    [InlineData(501, 500, 2, new[] { 500, 1 })]
    [InlineData(999, 500, 2, new[] { 500, 499 })]
    [InlineData(1000, 500, 2, new[] { 500, 500 })]
    [InlineData(1001, 500, 3, new[] { 500, 500, 1 })]
    [InlineData(1237, 250, 5, new[] { 250, 250, 250, 250, 237 })]
    public async Task ExecuteGroupedWithMetrics_OddAndBoundaryQuantities_DispatchesExactChunkSizes(
        int totalKeys, int chunkSize, int expectedQueries, int[] expectedChunkSizes)
    {
        var keys = Enumerable.Range(1, totalKeys).Select(i => $"K-{i}").ToList();
        var capturedChunkSizes = new List<int>();
        var lockObj = new object();

        var (results, dispatchedQueries) = await _executor.ExecuteGroupedWithMetricsAsync(
            keys,
            (chunk, _) =>
            {
                lock (lockObj)
                {
                    capturedChunkSizes.Add(chunk.Count);
                }
                var dict = chunk.ToDictionary(k => k, k => new List<string> { $"{k}-Val" });
                return Task.FromResult<IReadOnlyDictionary<string, List<string>>>(dict);
            },
            chunkSize: chunkSize);

        dispatchedQueries.ShouldBe(expectedQueries);
        results.Count.ShouldBe(totalKeys);

        // Verify captured chunk sizes match expected partitioning
        capturedChunkSizes.OrderByDescending(s => s).ShouldBe(expectedChunkSizes.OrderByDescending(s => s));
    }

    [Fact]
    public async Task ExecuteGrouped_WhenSomeKeysHaveNoChildren_PreservesAllKeysWithEmptyLists()
    {
        // 101 keys: Even keys have 2 items, odd keys have 0 items
        var keys = Enumerable.Range(1, 101).Select(i => $"KEY-{i}").ToList();

        var result = await _executor.ExecuteGroupedChunkedQueryAsync(
            keys,
            (chunk, _) =>
            {
                var dict = new Dictionary<string, List<string>>();
                foreach (var k in chunk)
                {
                    var num = int.Parse(k.Replace("KEY-", ""));
                    if (num % 2 == 0)
                    {
                        dict[k] = new List<string> { $"{k}-Child1", $"{k}-Child2" };
                    }
                    // Odd numbers are omitted from the returned dictionary
                }
                return Task.FromResult<IReadOnlyDictionary<string, List<string>>>(dict);
            },
            chunkSize: 25);

        result.Count.ShouldBe(101);

        for (int i = 1; i <= 101; i++)
        {
            var key = $"KEY-{i}";
            result.ContainsKey(key).ShouldBeTrue();
            if (i % 2 == 0)
            {
                result[key].Count.ShouldBe(2);
            }
            else
            {
                result[key].ShouldNotBeNull();
                result[key].ShouldBeEmpty();
            }
        }
    }

    #endregion

    #region Parameter Budget Provider with Odd Key Counts and Composite Keys

    [Fact]
    public async Task ParameterBudget_OddCompositeKeyQuantities_PartitionsCorrectly()
    {
        var budgetProvider = DatabaseParameterBudgetProvider.Instance;

        // SQLite: max 999 params.
        // 3 key columns, 20 context parameters, 50 safety buffer
        // Available: 999 - 20 - 50 = 929. Effective chunk size: 929 / 3 = 309.
        var chunkSize = budgetProvider.CalculateEffectiveChunkSize(
            defaultChunkSize: 500,
            keyColumnCount: 3,
            contextParameterCount: 20,
            dialect: DatabaseDialect.Sqlite,
            safetyBuffer: 50);

        chunkSize.ShouldBe(309);

        // Test with 619 keys (309 + 309 + 1 -> 3 queries)
        var keys = Enumerable.Range(1, 619).Select(i => $"COMPOSITE-KEY-{i}").ToList();
        var capturedChunks = new List<int>();
        var lockObj = new object();

        var (results, dispatchedQueries) = await _executor.ExecuteGroupedWithMetricsAsync(
            keys,
            (chunk, _) =>
            {
                lock (lockObj)
                {
                    capturedChunks.Add(chunk.Count);
                }
                var dict = chunk.ToDictionary(k => k, k => new List<string> { $"{k}-item" });
                return Task.FromResult<IReadOnlyDictionary<string, List<string>>>(dict);
            },
            chunkSize: chunkSize);

        dispatchedQueries.ShouldBe(3);
        results.Count.ShouldBe(619);
        capturedChunks.ShouldContain(309);
        capturedChunks.ShouldContain(1);
    }

    #endregion

    #region DataLoader Batch Evaluation with Odd Counts in GatewayExecutionService

    [Theory]
    [InlineData(17, 10, 2)]   // 10 + 7 -> 2 queries
    [InlineData(47, 20, 3)]   // 20 + 20 + 7 -> 3 queries
    [InlineData(153, 50, 4)]  // 50 + 50 + 50 + 3 -> 4 queries
    public async Task GatewayExecutionService_LoadInvoiceItemsBatchAsync_OddParentBatches_DispatchesExactQueries(
        int parentCount, int maxBatchSize, int expectedDispatchedQueries)
    {
        var eventBus = new InProcessChannelEventBus();
        var epochService = new EpochValidationService(eventBus: eventBus);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new ConsentCacheService(memoryCache, epochService, eventBus);

        var repoOptions = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = $"Data Source=odd_dataloader_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            }
        });
        using var repository = new SqliteGovernanceRepository(epochService, repoOptions);
        var resolutionService = new ConsentResolutionService();
        var maskingProvider = new ColumnMaskingProvider();

        var options = Options.Create(new GatewayOptions
        {
            GraphQL = new GraphQLOptions
            {
                MaxInClauseBatchSize = maxBatchSize
            }
        });

        var executor = new ChunkedQueryExecutor(defaultChunkSize: maxBatchSize);
        var executionService = new GatewayExecutionService(
            repository,
            resolutionService,
            cacheService,
            maskingProvider,
            executor,
            options);

        // Grant consent for finance_items
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var childMeta = await repository.GetTableMetadataAsync(childTableId);
        childMeta.ShouldNotBeNull();

        var userSid = new Sid("S-1-5-21-ODD-LOADER-USER");
        await repository.CreateConsentAsync(new Consent
        {
            TableId = childMeta.Table.Id,
            TableIdentifier = childTableId,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        });

        var claims = new[]
        {
            new Claim("objectSid", userSid.Value),
            new Claim(ClaimTypes.Role, "FinanceViewer")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var parentIds = Enumerable.Range(1, parentCount).Select(i => $"INV-ODD-{i}").ToList();

        // Act
        var result = await executionService.LoadInvoiceItemsBatchAsync(principal, parentIds);

        // Assert
        executionService.LastDispatchedChildQueryCount.ShouldBe(expectedDispatchedQueries);
        result.Count.ShouldBe(parentCount);
        result[$"INV-ODD-{parentCount}"].Count.ShouldBe(2);
    }

    #endregion
}
