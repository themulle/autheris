namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class RebacZanzibarTests
{
    private readonly GatewayOptions _options;
    private readonly InMemoryRebacStore _store;
    private readonly ZanzibarRebacEvaluator _evaluator;

    public RebacZanzibarTests()
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
    public async Task RebacEvaluator_EvaluatesDirectRelationship_Permitted()
    {
        // Arrange
        var tuple = new RebacTuple("tenant-acme", "user:alice", "viewer", "document:doc-101");
        await _store.AddTupleAsync(tuple);

        // Act
        var result = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-acme", "user:alice", "viewer", "document:doc-101"));

        // Assert
        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task SEC_REBAC_02_RebacStore_EnforcesStrictTenantBoundary_DeniesCrossTenantTupleAccess()
    {
        // Arrange: Alice has viewer on doc-101 in tenant-A
        await _store.AddTupleAsync(new RebacTuple("tenant-a", "user:alice", "viewer", "document:doc-101"));

        // Act: Alice checks viewer on doc-101 in tenant-B
        var resultTenantB = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-b", "user:alice", "viewer", "document:doc-101"));

        // Assert: Access must be denied across tenant boundary
        resultTenantB.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task SEC_REBAC_03_RebacEvaluator_CorrectlyInheritsPermissions_AcrossRelationHierarchy()
    {
        // Arrange: Owner inherits Editor, Editor inherits Viewer
        // Alice is assigned "owner" on doc-200
        await _store.AddTupleAsync(new RebacTuple("tenant-acme", "user:alice", "owner", "document:doc-200"));

        // Act
        var checkOwner = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-acme", "user:alice", "owner", "document:doc-200"));
        var checkEditor = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-acme", "user:alice", "editor", "document:doc-200"));
        var checkViewer = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-acme", "user:alice", "viewer", "document:doc-200"));
        var checkAdmin = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-acme", "user:alice", "superadmin", "document:doc-200"));

        // Assert: owner, editor and viewer are transitively permitted; undeclared superadmin is denied
        checkOwner.Allowed.ShouldBeTrue();
        checkEditor.Allowed.ShouldBeTrue();
        checkViewer.Allowed.ShouldBeTrue();
        checkAdmin.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task SEC_REBAC_01_RebacEvaluator_PreventsStackOverflow_OnCyclicRelationships_AndDenies()
    {
        // Arrange: create circular relationship docA parent docB parent docA
        await _store.AddTupleAsync(new RebacTuple("tenant-cycle", "document:docB", "parent", "document:docA"));
        await _store.AddTupleAsync(new RebacTuple("tenant-cycle", "document:docA", "parent", "document:docB"));

        // Act: Evaluating viewer on docA where user has no valid relation (triggers cycle)
        var result = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-cycle", "user:mallory", "viewer", "document:docA"));

        // Assert: Must not crash with StackOverflowException, must fail-closed (Allowed: false)
        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task SEC_REBAC_05_RebacEvaluator_InvalidatesCachedDecisions_WhenTuplesAreDeleted()
    {
        // Arrange
        var tuple = new RebacTuple("tenant-cache", "user:bob", "editor", "document:doc-300");
        await _store.AddTupleAsync(tuple);

        // 1. Initial check (caches Allowed = true)
        var initialCheck = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-cache", "user:bob", "editor", "document:doc-300"));
        initialCheck.Allowed.ShouldBeTrue();

        // 2. Delete tuple and invalidate cache
        await _store.DeleteTupleAsync(tuple);
        await _evaluator.InvalidateTenantCacheAsync("tenant-cache");

        // 3. Second check must reflect the revocation
        var revokedCheck = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-cache", "user:bob", "editor", "document:doc-300"));
        revokedCheck.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task RebacEvaluator_ResolvesHierarchicalParentRelationship()
    {
        // Arrange: Alice is viewer of "folder:finance". "document:budget2026" has parent "folder:finance".
        await _store.AddTupleAsync(new RebacTuple("tenant-acme", "user:alice", "viewer", "folder:finance"));
        await _store.AddTupleAsync(new RebacTuple("tenant-acme", "folder:finance", "parent", "document:budget2026"));

        // Act: Check if Alice has viewer access on document:budget2026
        var result = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-acme", "user:alice", "viewer", "document:budget2026"));

        // Assert: Parent relationship delegation grants access
        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task RebacEvaluator_ResolvesUserSetGroupMembership()
    {
        // Arrange: "group:engineers" has "viewer" on "repo:gateway". Bob is "member" of "group:engineers".
        await _store.AddTupleAsync(new RebacTuple("tenant-acme", "group:engineers#member", "viewer", "repo:gateway"));
        await _store.AddTupleAsync(new RebacTuple("tenant-acme", "user:bob", "member", "group:engineers"));

        // Act: Check if user:bob has viewer on repo:gateway
        var result = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-acme", "user:bob", "viewer", "repo:gateway"));

        // Assert: User-set expansion grants access
        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task SEC_REBAC_04_RebacDataLoader_BatchesMultipleTupleChecks_IntoSingleEvaluation()
    {
        // Arrange
        var dataLoader = new RebacBatchDataLoader(_evaluator, NullLogger<RebacBatchDataLoader>.Instance);

        await _store.AddTupleAsync(new RebacTuple("tenant-batch", "user:alice", "viewer", "doc:1"));
        await _store.AddTupleAsync(new RebacTuple("tenant-batch", "user:alice", "viewer", "doc:2"));
        // doc:3 is NOT granted

        var req1 = new RebacCheckRequest("tenant-batch", "user:alice", "viewer", "doc:1");
        var req2 = new RebacCheckRequest("tenant-batch", "user:alice", "viewer", "doc:2");
        var req3 = new RebacCheckRequest("tenant-batch", "user:alice", "viewer", "doc:3");
        var req1Duplicate = new RebacCheckRequest("tenant-batch", "user:alice", "viewer", "doc:1");

        // Act: Enqueue checks into DataLoader
        dataLoader.Enqueue(req1);
        dataLoader.Enqueue(req2);
        dataLoader.Enqueue(req3);
        dataLoader.Enqueue(req1Duplicate); // De-duplicated

        var batchDecisions = await dataLoader.ExecuteBatchAsync("tenant-batch");

        // Assert
        batchDecisions.Count.ShouldBe(3);
        batchDecisions[req1].ShouldBeTrue();
        batchDecisions[req2].ShouldBeTrue();
        batchDecisions[req3].ShouldBeFalse();
    }

    [Fact]
    public async Task CanQuery_IsInheritedFrom_Viewer_Editor_And_Owner()
    {
        await _store.AddTupleAsync(new RebacTuple("tenant-test", "user:david", "viewer", "table:lakehouse.dbo.orders"));
        await _store.AddTupleAsync(new RebacTuple("tenant-test", "user:ed", "editor", "table:sales.public.orders"));
        await _store.AddTupleAsync(new RebacTuple("tenant-test", "user:owen", "owner", "table:conf.client"));

        var davidCheck = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-test", "user:david", "can_query", "table:lakehouse.dbo.orders"));
        var edCheck = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-test", "user:ed", "can_query", "table:sales.public.orders"));
        var owenCheck = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-test", "user:owen", "can_query", "table:conf.client"));
        var strangerCheck = await _evaluator.CheckAsync(new RebacCheckRequest("tenant-test", "user:stranger", "can_query", "table:lakehouse.dbo.orders"));

        davidCheck.Allowed.ShouldBeTrue();
        edCheck.Allowed.ShouldBeTrue();
        owenCheck.Allowed.ShouldBeTrue();
        strangerCheck.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task CanQuery_WhenInheritCanQueryFromViewerDisabled_ViewerIsDenied_WhileEditorAndOwnerArePermitted()
    {
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = new GatewayOptions
        {
            Rebac = new RebacOptions
            {
                Enabled = true,
                InheritCanQueryFromViewer = false
            }
        };
        var evaluator = new ZanzibarRebacEvaluator(store, Options.Create(options), NullLogger<ZanzibarRebacEvaluator>.Instance);

        await store.AddTupleAsync(new RebacTuple("tenant-test", "user:david", "viewer", "table:lakehouse.dbo.orders"));
        await store.AddTupleAsync(new RebacTuple("tenant-test", "user:ed", "editor", "table:sales.public.orders"));
        await store.AddTupleAsync(new RebacTuple("tenant-test", "user:owen", "owner", "table:conf.client"));

        var davidCheck = await evaluator.CheckAsync(new RebacCheckRequest("tenant-test", "user:david", "can_query", "table:lakehouse.dbo.orders"));
        var edCheck = await evaluator.CheckAsync(new RebacCheckRequest("tenant-test", "user:ed", "can_query", "table:sales.public.orders"));
        var owenCheck = await evaluator.CheckAsync(new RebacCheckRequest("tenant-test", "user:owen", "can_query", "table:conf.client"));

        davidCheck.Allowed.ShouldBeFalse();
        edCheck.Allowed.ShouldBeTrue();
        owenCheck.Allowed.ShouldBeTrue();
    }
}
