using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Semantic;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>ISemanticManifestStore</c> (one manifest per tenant, saved by a single-statement upsert) across PostgreSQL and SQL Server engines.
/// The reserved semantic schema guard of the table metadata writer is pinned here as well, because it protects the virtual tables built from the manifest.
/// </summary>
public abstract class SemanticManifestContract : GovernanceContractBase
{
    private ISemanticManifestStore Manifests => Store.SemanticManifests;

    private static StoredSemanticManifest Sample(TenantId tenant, string version = "v1", string json = "{\"a\":1}", string hash = "h1") =>
        new(tenant, version, hash, 1, 2, 3, json, "admin", new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero));

    private static TableMetadata TableOf(string domain, string schema, string? documentationSource)
    {
        var id = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, schema, "orders"),
            Table = new Table
            {
                Id = id, SourceName = domain, SchemaName = schema, TableName = "orders", IsActive = true, DocumentationSource = documentationSource,
                DataSourceType = DataSourceType.Sql
            },
            Columns = [new TableColumn { TableId = id, ColumnName = "id", DataType = "int" }]
        };
    }

    [RequiresContainerRuntimeFact]
    public async Task Absent_ReturnsNull()
    {
        if (!Store.IsAvailable) return;

        (await Manifests.GetAsync(Store.TenantA, default)).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task SaveAndGet_RoundTripsEveryField()
    {
        if (!Store.IsAvailable) return;
        var sample = Sample(Store.TenantA);

        await Manifests.SaveAsync(Store.TenantA, sample, default);

        (await Manifests.GetAsync(Store.TenantA, default)).ShouldBe(sample);
    }

    [RequiresContainerRuntimeFact]
    public async Task Manifest_SurvivesStoreRecreation()
    {
        if (!Store.IsAvailable) return;
        await Manifests.SaveAsync(Store.TenantA, Sample(Store.TenantA), default);

        await using var second = await Store.CreateAdditionalStoreAsync();

        (await second.SemanticManifests.GetAsync(Store.TenantA, default))!.Version.ShouldBe("v1");
    }

    [RequiresContainerRuntimeFact]
    public async Task Save_WithMismatchingTenantInRecord_Throws()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentException>(() => Manifests.SaveAsync(Store.TenantB, Sample(Store.TenantA), default));

        (await Manifests.GetAsync(Store.TenantA, default)).ShouldBeNull();
        (await Manifests.GetAsync(Store.TenantB, default)).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task UserSchemaNamedLikeTenantHash_CannotBeCreated_AndTheSemanticDomainIsReserved()
    {
        if (!Store.IsAvailable) return;
        var hash = SemanticVirtualTables.TenantSchema(Store.TenantA);

        // A real table with a tenant-hash schema name, and a semantic-domain table without the server-set marker, are refused.
        await Should.ThrowAsync<GatewayInvalidQueryException>(() => Store.TableMetadata.UpsertTableMetadataAsync(TableOf("sales", hash, null), default));
        await Should.ThrowAsync<GatewayInvalidQueryException>(() => Store.TableMetadata.UpsertTableMetadataAsync(TableOf("semantic", hash, null), default));
        await Should.ThrowAsync<GatewayInvalidQueryException>(() => Store.TableMetadata.UpsertTableMetadataAsync(TableOf("semantic", "my_schema", "dbt"), default));

        // The ingestion service's own shape and ordinary tables are accepted.
        await Store.TableMetadata.UpsertTableMetadataAsync(TableOf("semantic", hash, "dbt"), default);
        await Store.TableMetadata.UpsertTableMetadataAsync(TableOf("sales", "public", null), default);
    }

    [RequiresContainerRuntimeFact]
    public async Task ConcurrentSaves_LeaveExactlyOneConsistentRow()
    {
        if (!Store.IsAvailable) return;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            Manifests.SaveAsync(Store.TenantA, Sample(Store.TenantA, $"v{i}", $"{{\"n\":{i}}}", $"h{i}"), default)));

        var loaded = await Manifests.GetAsync(Store.TenantA, default);
        loaded.ShouldNotBeNull();
        loaded.Json.ShouldBe($"{{\"n\":{loaded.Version[1..]}}}");
        loaded.ContentHash.ShouldBe($"h{loaded.Version[1..]}");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Manifests.SaveAsync(Store.TenantA, Sample(Store.TenantA), default);

        (await Manifests.GetAsync(Store.TenantB, default)).ShouldBeNull();
        (await Manifests.GetAsync(Store.TenantA, default))!.TenantId.ShouldBe(Store.TenantA);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Manifests.SaveAsync(Store.TenantA, Sample(Store.TenantA, "a1"), default);

        await Manifests.SaveAsync(Store.TenantB, Sample(Store.TenantB, "b1", "{\"b\":1}", "hb"), default);
        await Manifests.SaveAsync(Store.TenantB, Sample(Store.TenantB, "b2", "{\"b\":2}", "hb2"), default);

        (await Manifests.GetAsync(Store.TenantA, default))!.Version.ShouldBe("a1");
        (await Manifests.GetAsync(Store.TenantB, default))!.Version.ShouldBe("b2");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        Should.Throw<ArgumentException>(() => { _ = new TenantId("invalid tenant!"); });
        await Should.ThrowAsync<ArgumentException>(() => Manifests.GetAsync(default, default));
        await Should.ThrowAsync<ArgumentException>(() => Manifests.SaveAsync(default, Sample(default), default));
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var sample = Sample(Store.TenantA);

        await Manifests.SaveAsync(Store.TenantA, sample, default);
        await Manifests.SaveAsync(Store.TenantA, sample, default);
        await Manifests.SaveAsync(Store.TenantA, Sample(Store.TenantA, "v2", "{\"a\":2}", "h2"), default);

        var loaded = await Manifests.GetAsync(Store.TenantA, default);
        loaded!.Version.ShouldBe("v2");
        loaded.ContentHash.ShouldBe("h2");
        loaded.Json.ShouldBe("{\"a\":2}");
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        // The tenant key never raises a unique violation: concurrent first saves converge on one row.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Manifests.SaveAsync(Store.TenantB, Sample(Store.TenantB, $"v{i}"), default)));

        (await Manifests.GetAsync(Store.TenantB, default)).ShouldNotBeNull();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        // One manifest per tenant, no list: repeated reads are stable.
        await Manifests.SaveAsync(Store.TenantA, Sample(Store.TenantA), default);
        var first = await Manifests.GetAsync(Store.TenantA, default);
        var second = await Manifests.GetAsync(Store.TenantA, default);

        second.ShouldBe(first);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        // No page size exists; a large manifest document is stored and read back unchanged.
        var json = "{\"pad\":\"" + new string('x', 200_000) + "\"}";
        await Manifests.SaveAsync(Store.TenantA, Sample(Store.TenantA, "big", json, "hbig"), default);

        (await Manifests.GetAsync(Store.TenantA, default))!.Json.ShouldBe(json);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        // No soft delete: a tenant that never imported a manifest sees none, and a replaced manifest is never readable again.
        await Manifests.SaveAsync(Store.TenantA, Sample(Store.TenantA, "old", "{\"old\":1}", "ho"), default);
        await Manifests.SaveAsync(Store.TenantA, Sample(Store.TenantA, "new", "{\"new\":1}", "hn"), default);

        (await Manifests.GetAsync(Store.TenantB, default)).ShouldBeNull();
        (await Manifests.GetAsync(Store.TenantA, default))!.Json.ShouldBe("{\"new\":1}");
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlSemanticManifestContractTests(GovernancePostgresFixture fixture) : SemanticManifestContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "semantic_manifest_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerSemanticManifestContractTests(GovernanceSqlServerFixture fixture) : SemanticManifestContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "semantic_manifest_contract");
}
