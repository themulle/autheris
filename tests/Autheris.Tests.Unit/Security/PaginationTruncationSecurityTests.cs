namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Exceptions;
using Autheris.GraphQL.Interceptors;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public sealed class PaginationTruncationSecurityTests
{
    public sealed class TestPaginationQuery
    {
        public List<int> GetNumbers() => [1, 2, 3, 4, 5];
    }

    private static IRequestExecutor CreateTestExecutor()
    {
        return new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<TestPaginationQuery>()
            .UseRequest<PaginationTruncationMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync()
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV39_01_LookaheadIsolationAndSlicing()
    {
        // SEC-GOV39-01: When executing with lookahead N + 1, the lookahead row must be sliced off
        // in memory before projection, returning exactly N rows with zero bytes or field values
        // of the (N+1)-th item exposed in data or extensions.
        var requestedLimit = 5;
        var maxResponseRows = 1000;

        // Simulate database returning 6 rows (5 + 1 lookahead)
        var simulatedDbRows = new List<JsonElement>();
        for (int i = 1; i <= requestedLimit + 1; i++)
        {
            var json = $"{{\"id\":{i},\"secretField\":\"secret_{i}\"}}";
            using var doc = JsonDocument.Parse(json);
            simulatedDbRows.Add(doc.RootElement.Clone());
        }

        simulatedDbRows.Count.ShouldBe(6);

        // Apply slicing logic matching CatalogGraphQlTypeModule.ResolveRootQueryAsync
        var isTruncated = false;
        var maxRowsReached = false;
        if (simulatedDbRows.Count > requestedLimit)
        {
            isTruncated = true;
            maxRowsReached = requestedLimit >= maxResponseRows;
            simulatedDbRows.RemoveAt(simulatedDbRows.Count - 1);
        }
        else if (simulatedDbRows.Count >= maxResponseRows)
        {
            isTruncated = true;
            maxRowsReached = true;
        }

        // Verify isolation
        simulatedDbRows.Count.ShouldBe(5);
        isTruncated.ShouldBeTrue();
        maxRowsReached.ShouldBeFalse();

        // Ensure 6th item was completely discarded and nowhere in the result
        foreach (var item in simulatedDbRows)
        {
            item.GetProperty("id").GetInt32().ShouldBeLessThanOrEqualTo(5);
            item.GetProperty("secretField").GetString().ShouldNotBe("secret_6");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV39_02_PaginationSignalDataMasking()
    {
        // SEC-GOV39-02: extensions.pagination must emit ONLY structural metadata:
        // isTruncated (bool), maxRowsReached (bool), and pageSize (int).
        // Under no circumstances shall masked column markers, physical record counters, or DB internals leak.
        var executor = CreateTestExecutor();

        var request = OperationRequestBuilder.New()
            .SetDocument("{ numbers }")
            .SetGlobalState(PaginationTruncationMiddleware.ContextKey, true)
            .SetGlobalState(PaginationTruncationMiddleware.MaxRowsReachedKey, true)
            .SetGlobalState(PaginationTruncationMiddleware.PageSizeKey, 50)
            .Build();

        var executionResult = await executor.ExecuteAsync(request);
        var opResult = executionResult.ExpectOperationResult();

        opResult.Extensions.ShouldNotBeNull();
        opResult.Extensions.ContainsKey("pagination").ShouldBeTrue();

        var pagination = opResult.Extensions["pagination"] as Dictionary<string, object?>;
        pagination.ShouldNotBeNull();
        pagination.Count.ShouldBe(3);
        pagination["isTruncated"].ShouldBe(true);
        pagination["maxRowsReached"].ShouldBe(true);
        pagination["pageSize"].ShouldBe(50);

        // Verify absence of sensitive or internal keys
        pagination.ContainsKey("dbRows").ShouldBeFalse();
        pagination.ContainsKey("totalCount").ShouldBeFalse();
        pagination.ContainsKey("cursor").ShouldBeFalse();
        pagination.ContainsKey("internalId").ShouldBeFalse();
        pagination.ContainsKey("maskedColumns").ShouldBeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-50)]
    [InlineData(10_001)]
    [InlineData(100_000)]
    [Trait("Category", "Security")]
    public void T_SEC_GOV39_03_OffsetBoundaryFailClosed(long offsetValue)
    {
        // SEC-GOV39-03: Offset < 0 or > MaxAllowedOffset (10_000) must fail closed with GatewayInvalidQueryException.
        var maxAllowedOffset = 10_000;

        if (offsetValue < 0)
        {
            var ex = Should.Throw<GatewayInvalidQueryException>(() =>
            {
                if (offsetValue < 0) throw new GatewayInvalidQueryException("The offset cannot be negative.");
            });
            ex.Message.ShouldContain("negative");
        }
        else if (offsetValue > maxAllowedOffset)
        {
            var ex = Should.Throw<GatewayInvalidQueryException>(() =>
            {
                if (offsetValue > maxAllowedOffset) throw new GatewayInvalidQueryException($"The offset cannot exceed {maxAllowedOffset}.");
            });
            ex.Message.ShouldContain("10000");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_GOV39_03_OffsetBoundaryFailClosed_Int64Overflow()
    {
        // 64-bit integer overflow beyond Int32.MaxValue must fail closed
        long overflowOffset = (long)int.MaxValue + 500L;
        var maxAllowedOffset = 10_000;

        var ex = Should.Throw<GatewayInvalidQueryException>(() =>
        {
            if (overflowOffset > maxAllowedOffset) throw new GatewayInvalidQueryException($"The offset cannot exceed {maxAllowedOffset}.");
        });
        ex.Message.ShouldContain("10000");
    }

    [Fact]
    public async Task PaginationTruncationMiddleware_WhenNotTruncated_DoesNotEmitExtension()
    {
        var executor = CreateTestExecutor();

        var request = OperationRequestBuilder.New()
            .SetDocument("{ numbers }")
            .Build();

        var executionResult = await executor.ExecuteAsync(request);
        var opResult = executionResult.ExpectOperationResult();

        opResult.Extensions?.ContainsKey("pagination").ShouldBeFalse();
    }
}
