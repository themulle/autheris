namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

public sealed class RebacHierarchyAndFailClosedTests
{
    private readonly GatewayOptions _options;
    private readonly InMemoryRebacStore _store;
    private readonly ZanzibarRebacEvaluator _evaluator;

    public RebacHierarchyAndFailClosedTests()
    {
        _options = new GatewayOptions
        {
            Rebac = new RebacOptions
            {
                Enabled = true,
                MaxTraversalDepth = 5,
                CacheTtlSeconds = 10
            }
        };

        _store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        _evaluator = new ZanzibarRebacEvaluator(_store, Options.Create(_options), NullLogger<ZanzibarRebacEvaluator>.Instance);
    }

    [Fact]
    public void RebacTableGate_CanonicalObjectIds_FormatCorrectly()
    {
        var table = new TableIdentifier("sales", "analytics", "orders");

        RebacTableGate.ObjectId(table).ShouldBe("table:sales.analytics.orders");
        RebacTableGate.SchemaObjectId("sales", "analytics").ShouldBe("schema:sales.analytics");
        RebacTableGate.SchemaObjectId(table).ShouldBe("schema:sales.analytics");
        RebacTableGate.DomainObjectId("sales").ShouldBe("domain:sales");
        RebacTableGate.DomainObjectId(table).ShouldBe("domain:sales");
    }

    [Fact]
    public async Task ZanzibarEvaluator_SchemaLevelPermission_AllowsTableAccess()
    {
        // Arrange
        // Structure tuple: schema:sales.analytics is parent of table:sales.analytics.orders
        await _store.AddTupleAsync(new RebacTuple("sales", "schema:sales.analytics", "parent", "table:sales.analytics.orders"));
        // Permission tuple: user:alice can_query schema:sales.analytics
        await _store.AddTupleAsync(new RebacTuple("sales", "user:alice", "can_query", "schema:sales.analytics"));

        // Act
        var result = await _evaluator.CheckAsync(new RebacCheckRequest("sales", "user:alice", "can_query", "table:sales.analytics.orders"));

        // Assert
        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task ZanzibarEvaluator_DomainLevelPermission_AllowsTableAccess()
    {
        // Arrange
        // Structure tuples: domain:sales parent schema:sales.analytics, schema:sales.analytics parent table:sales.analytics.orders
        await _store.AddTupleAsync(new RebacTuple("sales", "domain:sales", "parent", "schema:sales.analytics"));
        await _store.AddTupleAsync(new RebacTuple("sales", "schema:sales.analytics", "parent", "table:sales.analytics.orders"));
        // Permission tuple: user:bob can_query domain:sales
        await _store.AddTupleAsync(new RebacTuple("sales", "user:bob", "can_query", "domain:sales"));

        // Act
        var result = await _evaluator.CheckAsync(new RebacCheckRequest("sales", "user:bob", "can_query", "table:sales.analytics.orders"));

        // Assert
        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task ZanzibarEvaluator_DomainIsolation_DeniesCrossDomainAccessEvenWithSameSchemaName()
    {
        // Arrange: d1 has schema 'raw' and d2 has schema 'raw'
        await _store.AddTupleAsync(new RebacTuple("d1", "schema:d1.raw", "parent", "table:d1.raw.orders"));
        await _store.AddTupleAsync(new RebacTuple("d2", "schema:d2.raw", "parent", "table:d2.raw.orders"));

        // Alice is granted on schema:d1.raw in d1
        await _store.AddTupleAsync(new RebacTuple("d1", "user:alice", "can_query", "schema:d1.raw"));

        // Act: Alice attempts to query d2.raw.orders in tenant d2
        var result = await _evaluator.CheckAsync(new RebacCheckRequest("d2", "user:alice", "can_query", "table:d2.raw.orders"));

        // Assert: Access must be denied
        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ZanzibarEvaluator_NoTuplesForTenant_FailsClosed()
    {
        // Act: Query tenant with zero tuples
        var result = await _evaluator.CheckAsync(new RebacCheckRequest("empty-tenant", "user:alice", "can_query", "table:empty-tenant.schema.orders"));

        // Assert
        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ZanzibarEvaluator_ParentTupleWithEmptyUser_FailsClosed()
    {
        // Arrange: Store returns corrupt structure tuple with whitespace/empty parent
        var store = Substitute.For<IRebacStore>();
        store.GetTuplesAsync("sales", user: null, relation: "parent", obj: "table:sales.analytics.orders", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<RebacTuple>>([new RebacTuple("sales", "   ", "parent", "table:sales.analytics.orders")]));

        var evaluator = new ZanzibarRebacEvaluator(store, Options.Create(_options), NullLogger<ZanzibarRebacEvaluator>.Instance);

        // Act
        var result = await evaluator.CheckAsync(new RebacCheckRequest("sales", "user:alice", "can_query", "table:sales.analytics.orders"));

        // Assert
        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ZanzibarEvaluator_StoreThrowsException_FailsClosedWithoutUnhandledException()
    {
        // Arrange: Failing store
        var failingStore = Substitute.For<IRebacStore>();
        failingStore.GetTuplesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("Redis connection timed out"));

        var evaluator = new ZanzibarRebacEvaluator(failingStore, Options.Create(_options), NullLogger<ZanzibarRebacEvaluator>.Instance);

        // Act
        var result = await evaluator.CheckAsync(new RebacCheckRequest("sales", "user:alice", "can_query", "table:sales.analytics.orders"));

        // Assert: Must fail-closed and return Denied
        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ZanzibarEvaluator_CycleInParentRelations_FailsClosedAndTerminates()
    {
        // Arrange: Cycle between schema and table
        await _store.AddTupleAsync(new RebacTuple("sales", "schema:sales.analytics", "parent", "table:sales.analytics.orders"));
        await _store.AddTupleAsync(new RebacTuple("sales", "table:sales.analytics.orders", "parent", "schema:sales.analytics"));

        // Act
        var result = await _evaluator.CheckAsync(new RebacCheckRequest("sales", "user:alice", "can_query", "table:sales.analytics.orders"));

        // Assert
        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ZanzibarEvaluator_DeepChainExceedingMaxTraversalDepth_FailsClosedAndTerminates()
    {
        // Arrange: Chain longer than MaxTraversalDepth (5)
        var options = new GatewayOptions
        {
            Rebac = new RebacOptions { Enabled = true, MaxTraversalDepth = 2 }
        };
        var evaluator = new ZanzibarRebacEvaluator(_store, Options.Create(options), NullLogger<ZanzibarRebacEvaluator>.Instance);

        // t1 -> t2 -> t3 -> t4
        await _store.AddTupleAsync(new RebacTuple("t", "obj:3", "parent", "obj:4"));
        await _store.AddTupleAsync(new RebacTuple("t", "obj:2", "parent", "obj:3"));
        await _store.AddTupleAsync(new RebacTuple("t", "obj:1", "parent", "obj:2"));
        await _store.AddTupleAsync(new RebacTuple("t", "user:alice", "can_query", "obj:1"));

        // Act
        var result = await evaluator.CheckAsync(new RebacCheckRequest("t", "user:alice", "can_query", "obj:4"));

        // Assert
        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task RebacTableGate_IsAllowedAsync_FailsClosedWhenStoreThrows()
    {
        var failingStore = Substitute.For<IRebacStore>();
        failingStore.GetTuplesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));

        var evaluator = new ZanzibarRebacEvaluator(failingStore, Options.Create(_options), NullLogger<ZanzibarRebacEvaluator>.Instance);

        var allowed = await RebacTableGate.IsAllowedAsync(
            evaluator,
            new TenantId("sales"),
            new Sid("user:alice"),
            new TableIdentifier("sales", "analytics", "orders"),
            CancellationToken.None);

        allowed.ShouldBeFalse();
    }
}
