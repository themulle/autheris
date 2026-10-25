namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Messaging;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogAtScaleSecurityTests : IAsyncDisposable
{
    private readonly SqliteGovernanceRepository _repo;
    private readonly InProcessChannelEventBus _eventBus;

    public CatalogAtScaleSecurityTests()
    {
        var epoch = Substitute.For<IEpochValidationService>();
        _eventBus = new InProcessChannelEventBus();
        _repo = GovernanceStoreFactory.CreateSqlite(epoch);
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _eventBus.DisposeAsync();
    }

    [Fact]
    public async Task T_CAT138_20_MonotonicVersionBumpsOnMutations()
    {
        // 1. Initial version is (1, 1)
        var initialVer = await _repo.GetCatalogVersionAsync();
        initialVer.StructureVersion.ShouldBe(1);
        initialVer.ContentVersion.ShouldBe(1);

        // 2. Structural mutation: UpsertTableMetadata bumps structure_version
        var tableId = Guid.NewGuid();
        var tableMeta = new TableMetadata
        {
            Table = new Table
            {
                Id = tableId,
                SourceType = "sqlite",
                SourceName = "sales",
                SchemaName = "crm",
                TableName = "customers",
                DisplayName = "Customers",
                Sensitivity = "NORMAL",
                IsActive = true
            },
            Identifier = new TableIdentifier("sales", "crm", "customers"),
            Columns = new List<TableColumn>
            {
                new() { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "id", DataType = "TEXT" },
                new() { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "name", DataType = "TEXT" }
            },
            PrimaryKeyColumns = new[] { "id" }
        };

        await _repo.UpsertTableMetadataAsync(tableMeta);
        var postUpsertVer = await _repo.GetCatalogVersionAsync();
        postUpsertVer.StructureVersion.ShouldBe(2);
        postUpsertVer.ContentVersion.ShouldBe(1);

        // 3. Content mutation: SetDatasetVisibility bumps content_version
        var change = await _repo.SetDatasetVisibilityAsync(
            tableMeta.Identifier, DatasetVisibility.Full, new Sid("S-1-5-21-test"));
        change.ShouldNotBeNull();
        change.Changed.ShouldBeTrue();

        var postVisibilityVer = await _repo.GetCatalogVersionAsync();
        postVisibilityVer.StructureVersion.ShouldBe(2);
        postVisibilityVer.ContentVersion.ShouldBe(2);

        // 4. Create second table for relation
        var ordersTableId = Guid.NewGuid();
        var ordersMeta = new TableMetadata
        {
            Table = new Table
            {
                Id = ordersTableId,
                SourceType = "sqlite",
                SourceName = "sales",
                SchemaName = "crm",
                TableName = "orders",
                DisplayName = "Orders",
                Sensitivity = "NORMAL",
                IsActive = true
            },
            Identifier = new TableIdentifier("sales", "crm", "orders"),
            Columns = new List<TableColumn>
            {
                new() { Id = Guid.NewGuid(), TableId = ordersTableId, ColumnName = "id", DataType = "TEXT" },
                new() { Id = Guid.NewGuid(), TableId = ordersTableId, ColumnName = "customer_id", DataType = "TEXT" }
            },
            PrimaryKeyColumns = new[] { "id" }
        };
        await _repo.UpsertTableMetadataAsync(ordersMeta);

        // 5. Structural mutation: CreateRelation bumps structure_version
        var rel = new TableRelation
        {
            Id = Guid.NewGuid(),
            ParentTableId = tableId,
            ChildTableId = ordersTableId,
            RelationName = "FK_Customers_Orders",
            Cardinality = RelationCardinality.OneToMany,
            ParentTableIdentifier = tableMeta.Identifier,
            ChildTableIdentifier = ordersMeta.Identifier,
            JoinKeysParent = new[] { "id" },
            JoinKeysChild = new[] { "customer_id" }
        };

        var relResult = await _repo.CreateRelationAsync(rel);
        relResult.Created.ShouldBeTrue();

        var postRelationVer = await _repo.GetCatalogVersionAsync();
        postRelationVer.StructureVersion.ShouldBeGreaterThan(2);
        postRelationVer.ContentVersion.ShouldBe(2);
    }

    [Fact]
    public async Task T_CAT138_21_DbCatalogVersionProvider_CachesAndInvalidatesOnEvent()
    {
        var mockRepo = Substitute.For<IGovernanceRepository>();
        mockRepo.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new CatalogVersion(1, 1), new CatalogVersion(2, 1));

        Func<object, Task>? handler = null;
        var subscription = Substitute.For<IDisposable>();
        var eventBus = Substitute.For<IEventBus>();
        eventBus.Subscribe<object>(ICatalogVersionProvider.CatalogChangedEventChannel, Arg.Do<Func<object, Task>>(h => handler = h))
            .Returns(subscription);
        var provider = new DbCatalogVersionProvider(mockRepo, eventBus, ttl: TimeSpan.FromSeconds(60));
        handler.ShouldNotBeNull();

        // First call loads from repo
        var v1 = await provider.GetCatalogVersionAsync();
        v1.StructureVersion.ShouldBe(1);

        // Second call hits cache (no new repo call)
        var v2 = await provider.GetCatalogVersionAsync();
        v2.StructureVersion.ShouldBe(1);
        await mockRepo.Received(1).GetCatalogVersionAsync(Arg.Any<CancellationToken>());

        // Deliver the catalog:changed event to the registered handler directly (no async bus involved)
        await handler(new CatalogChangedEvent(2, 1, DateTimeOffset.UtcNow));

        // Third call reloads fresh version
        var v3 = await provider.GetCatalogVersionAsync();
        v3.StructureVersion.ShouldBe(2);
        await mockRepo.Received(2).GetCatalogVersionAsync(Arg.Any<CancellationToken>());

        provider.Dispose();
        subscription.Received(1).Dispose();
    }

    [Fact]
    public void T_CAT138_22_CatalogSnapshot_DenseOrdinalsAndO1Lookups()
    {
        var t1Id = Guid.NewGuid();
        var t2Id = Guid.NewGuid();
        var id1 = new TableIdentifier("sales", "crm", "t1");
        var id2 = new TableIdentifier("sales", "crm", "t2");

        var meta1 = new TableMetadata
        {
            Table = new Table { Id = t1Id, SourceName = "sales", SchemaName = "crm", TableName = "t1", DisplayName = "T1", Sensitivity = "NORMAL" },
            Identifier = id1,
            Columns = Array.Empty<TableColumn>()
        };
        var meta2 = new TableMetadata
        {
            Table = new Table { Id = t2Id, SourceName = "sales", SchemaName = "crm", TableName = "t2", DisplayName = "T2", Sensitivity = "NORMAL" },
            Identifier = id2,
            Columns = Array.Empty<TableColumn>()
        };

        var rel = new TableRelation
        {
            Id = Guid.NewGuid(),
            ParentTableId = t1Id,
            ChildTableId = t2Id,
            RelationName = "rel1",
            Cardinality = RelationCardinality.OneToMany,
            ParentTableIdentifier = id1,
            ChildTableIdentifier = id2,
            JoinKeysParent = new[] { "id" },
            JoinKeysChild = new[] { "t1_id" }
        };

        var snapshot = CatalogSnapshot.Create(
            new CatalogVersion(3, 4),
            new[] { meta1, meta2 },
            new[] { rel },
            degradedEntityCount: 1);

        snapshot.Version.StructureVersion.ShouldBe(3);
        snapshot.Version.ContentVersion.ShouldBe(4);
        snapshot.DegradedEntityCount.ShouldBe(1);
        snapshot.TableCount.ShouldBe(2);

        // Ordinal lookup
        snapshot.TryGetOrdinal(id1, out var ord1).ShouldBeTrue();
        ord1.ShouldBe(0);
        snapshot.TryGetOrdinal(id2, out var ord2).ShouldBeTrue();
        ord2.ShouldBe(1);

        // O(1) Relation lookup
        var parentRels = snapshot.GetParentRelations(t1Id);
        parentRels.Count.ShouldBe(1);
        parentRels[0].RelationName.ShouldBe("rel1");

        var childRels = snapshot.GetChildRelations(t2Id);
        childRels.Count.ShouldBe(1);
        childRels[0].RelationName.ShouldBe("rel1");
    }

    [Fact]
    public async Task T_CAT138_23_CatalogSnapshotProvider_MonotonicityGuardRejectsStaleVersions()
    {
        var mockRepo = Substitute.For<IGovernanceRepository>();
        var verProvider = Substitute.For<ICatalogVersionProvider>();

        mockRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<TableMetadata>());
        mockRepo.GetAllRelationsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<TableRelation>());

        // Initial version (5, 5)
        verProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new CatalogVersion(5, 5));

        using var provider = new CatalogSnapshotProvider(mockRepo, verProvider);
        await provider.RefreshAsync();
        provider.CurrentSnapshot.Version.StructureVersion.ShouldBe(5);
        provider.CurrentSnapshot.Version.ContentVersion.ShouldBe(5);

        // Stale/regressed version reported (4, 4)
        verProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new CatalogVersion(4, 4));

        await provider.RefreshAsync();

        // Must NOT regress (fail-closed, preserves existing snapshot)
        provider.CurrentSnapshot.Version.StructureVersion.ShouldBe(5);
        provider.CurrentSnapshot.Version.ContentVersion.ShouldBe(5);
    }

    [Fact]
    public void T_CAT138_24_CatalogVisibleBitSet_OperationsAndPopCount()
    {
        var bitset = new CatalogVisibleBitSet(100);
        bitset.Capacity.ShouldBe(100);
        bitset.SetCount.ShouldBe(0);

        bitset.Set(0);
        bitset.Set(42);
        bitset.Set(63);
        bitset.Set(64);
        bitset.Set(99);

        bitset.IsSet(0).ShouldBeTrue();
        bitset.IsSet(42).ShouldBeTrue();
        bitset.IsSet(63).ShouldBeTrue();
        bitset.IsSet(64).ShouldBeTrue();
        bitset.IsSet(99).ShouldBeTrue();
        bitset.IsSet(1).ShouldBeFalse();
        bitset.IsSet(65).ShouldBeFalse();

        bitset.SetCount.ShouldBe(5);

        // Setting already set bit does not increment count
        bitset.Set(42);
        bitset.SetCount.ShouldBe(5);

        // Out of range throws
        Should.Throw<ArgumentOutOfRangeException>(() => bitset.Set(100));

        // AllSet factory
        var all = CatalogVisibleBitSet.AllSet(70);
        all.Capacity.ShouldBe(70);
        all.SetCount.ShouldBe(70);
        all.IsSet(0).ShouldBeTrue();
        all.IsSet(69).ShouldBeTrue();
        all.IsSet(70).ShouldBeFalse();
    }

    [Fact]
    public void T_CAT138_25_CatalogVisibilityCache_ValidationAndMultiEpochInvalidation()
    {
        var cache = new CatalogVisibilityLruCache(ttl: TimeSpan.FromSeconds(30), maxEntries: 100);
        var key1 = new CatalogVisibilityCacheKey("S-1-5-21-user1", "sales", 1, 1, 1, 1);
        var bitset1 = new CatalogVisibleBitSet(10);
        bitset1.Set(0);

        cache.Set(key1, bitset1);

        cache.TryGet(key1, out var retrieved).ShouldBeTrue();
        retrieved.ShouldNotBeNull();
        retrieved.IsSet(0).ShouldBeTrue();

        // Increment structure_version in key -> miss (multi-epoch invalidation)
        var key2 = new CatalogVisibilityCacheKey("S-1-5-21-user1", "sales", 2, 1, 1, 1);
        cache.TryGet(key2, out _).ShouldBeFalse();

        // InvalidateAll clears
        cache.InvalidateAll();
        cache.TryGet(key1, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task T_CAT138_26_CatalogSearchEngine_PreFiltersBeforeScoring()
    {
        var options = Options.Create(new CatalogSearchOptions
        {
            EmbeddingProvider = CatalogEmbeddingProvider.None,
            RrfConstantK = 60
        });

        var engine = new CatalogSearchEngine(
            NoneEmbeddingGenerator.Instance,
            options,
            NullLogger<CatalogSearchEngine>.Instance);

        var idAllowed = new TableIdentifier("sales", "crm", "allowed_table");
        var idForbidden = new TableIdentifier("sales", "crm", "forbidden_table");

        var metaAllowed = new TableMetadata
        {
            Table = new Table { SourceName = "sales", SchemaName = "crm", TableName = "allowed_table", DisplayName = "Allowed Customers", Sensitivity = "NORMAL", Description = "Customers account info" },
            Identifier = idAllowed,
            Columns = new List<TableColumn> { new() { ColumnName = "customer_name", DataType = "TEXT" } }
        };
        var metaForbidden = new TableMetadata
        {
            Table = new Table { SourceName = "sales", SchemaName = "crm", TableName = "forbidden_table", DisplayName = "Forbidden Customers", Sensitivity = "NORMAL", Description = "Secret Customers records" },
            Identifier = idForbidden,
            Columns = new List<TableColumn> { new() { ColumnName = "secret_id", DataType = "TEXT" } }
        };

        await engine.RebuildIndexAsync(new[] { metaAllowed, metaForbidden });

        // Search query matching "Customers"
        var query = new CatalogSearchQuery("Customers", Limit: 10, Mode: CatalogSearchMode.KeywordBm25);

        // Pre-filter allows ONLY idAllowed
        var hits = engine.Search(query, id => id.Equals(idAllowed));

        hits.Count.ShouldBe(1);
        hits[0].TableIdentifier.ShouldBe(idAllowed);
        hits[0].EffectiveMode.ShouldBe("keyword");
        hits.Any(h => h.TableIdentifier.Equals(idForbidden)).ShouldBeFalse();
    }

    [Fact]
    public void T_CAT138_27_LocalOnnx_IntegrityAndPathTraversal()
    {
        // 1. Path traversal is rejected
        Should.Throw<ArgumentException>(() =>
            LocalOnnxEmbeddingGenerator.ValidateModelFileIntegrity("../../models/onnx.bin", "abcd"));

        // 2. Non-existent file returns false
        LocalOnnxEmbeddingGenerator.ValidateModelFileIntegrity("/tmp/nonexistent_model.onnx", "abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234")
            .ShouldBeFalse();

        // 3. Constant-time SHA-256 validation with temporary file
        var tempFile = Path.GetTempFileName();
        try
        {
            var content = "dummy-onnx-model-content"u8.ToArray();
            File.WriteAllBytes(tempFile, content);

            var expectedSha256 = Convert.ToHexString(SHA256.HashData(content));

            // Matching hash succeeds
            LocalOnnxEmbeddingGenerator.ValidateModelFileIntegrity(tempFile, expectedSha256).ShouldBeTrue();

            // Tampered hash fails
            var tamperedSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
            LocalOnnxEmbeddingGenerator.ValidateModelFileIntegrity(tempFile, tamperedSha256).ShouldBeFalse();
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void T_CAT138_28_Ollama_SsrfAndSecretScrubbing()
    {
        // 1. SSRF rejection of cloud metadata IP
        OllamaEmbeddingGenerator.ValidateEndpoint("http://169.254.169.254/api/embeddings").ShouldBeFalse();
        OllamaEmbeddingGenerator.ValidateEndpoint("http://instance-data.internal/api/embeddings").ShouldBeFalse();
        OllamaEmbeddingGenerator.ValidateEndpoint("ftp://ollama.local/api/embeddings").ShouldBeFalse();

        // 2. Valid HTTP/HTTPS endpoint allowed
        OllamaEmbeddingGenerator.ValidateEndpoint("http://localhost:11434").ShouldBeTrue();
        OllamaEmbeddingGenerator.ValidateEndpoint("https://ollama.internal.net:11434").ShouldBeTrue();

        // 3. Secret scrubbing on prompt
        var sensitivePrompt = "Find customer data with api_key=sk-1234567890abcdef12345678 and token=ghp_ABC1234567890";
        var redacted = SecretScrubber.Redact(sensitivePrompt);
        redacted.ShouldNotContain("sk-1234567890abcdef12345678");
        redacted.ShouldNotContain("ghp_ABC1234567890");
    }
}
