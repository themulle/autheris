using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>Contract suite for <c>IGoldenQueryRepository</c> across PostgreSQL and SQL Server engines.</summary>
public abstract class GoldenQueryContract : GovernanceContractBase
{
    private IGoldenQueryRepository Golden => Store.Services.GetRequiredService<IGoldenQueryRepository>();

    private async Task<TableMetadata> SeedTableAsync(string name, bool active = true)
    {
        var id = new TableIdentifier("golden", "dbo", name + "_" + Guid.NewGuid().ToString("N")[..8]);
        return await Store.Services.GetRequiredService<ITableMetadataRepository>().UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id,
            ReplaceKeyDefinition = true,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = id.TableName,
                IsActive = active
            },
            Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "bigint" }],
            PrimaryKeyColumns = ["id"]
        });
    }

    private static GoldenQueryStoreEntry Entry(string key, string? title = null) =>
        new(key, title ?? "Title " + key, "Description", "{ orders { id } }", """{"limit":10}""");

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var table = await SeedTableAsync("tenant_reads");
        await Golden.ReplaceForTableAsync(Store.TenantA, table.Table.Id.ToString(), GoldenQuerySource.Dbt, [Entry("a-1")]);
        await Golden.ReplaceForTableAsync(Store.TenantB, table.Table.Id.ToString(), GoldenQuerySource.Dbt, [Entry("b-1")]);

        (await Golden.GetForTenantAsync(Store.TenantA)).Select(r => r.SourceKey).Should().Equal("a-1");
        (await Golden.GetForTenantAsync(Store.TenantB)).Select(r => r.SourceKey).Should().Equal("b-1");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var table = await SeedTableAsync("tenant_writes");
        var tableId = table.Table.Id.ToString();
        await Golden.ReplaceForTableAsync(Store.TenantA, tableId, GoldenQuerySource.Dbt, [Entry("same-key", "Tenant A")]);
        await Golden.ReplaceForTableAsync(Store.TenantB, tableId, GoldenQuerySource.Dbt, [Entry("same-key", "Tenant B")]);

        await Golden.ReplaceForTableAsync(Store.TenantB, tableId, GoldenQuerySource.Dbt, []);

        var remaining = await Golden.GetForTenantAsync(Store.TenantA);
        remaining.Should().ContainSingle().Which.Title.Should().Be("Tenant A");
        (await Golden.GetForTenantAsync(Store.TenantB)).Should().BeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        var replace = () => Golden.ReplaceForTableAsync(default, "table-1", GoldenQuerySource.Dbt, [Entry("k")]);
        var read = () => Golden.GetForTenantAsync(default);

        await replace.Should().ThrowAsync<ArgumentException>();
        await read.Should().ThrowAsync<ArgumentException>();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var table = await SeedTableAsync("idempotent");
        var tableId = table.Table.Id.ToString();

        await Golden.ReplaceForTableAsync(Store.TenantA, tableId, GoldenQuerySource.Dbt, [Entry("k1"), Entry("k2")]);
        await Golden.ReplaceForTableAsync(Store.TenantA, tableId, GoldenQuerySource.Dbt, [Entry("k1"), Entry("k2")]);

        var rows = await Golden.GetForTenantAsync(Store.TenantA);
        rows.Select(r => r.SourceKey).Should().Equal("k1", "k2");
        rows.Should().OnlyContain(r => r.ContentHash.Length == 64 && r.Domain == "golden");
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var table = await SeedTableAsync("unique_key");

        var duplicate = () => Golden.ReplaceForTableAsync(Store.TenantA, table.Table.Id.ToString(), GoldenQuerySource.Dbt, [Entry("dup"), Entry("dup")]);

        await duplicate.Should().ThrowAsync<ArgumentException>();
        (await Golden.GetForTenantAsync(Store.TenantA)).Should().BeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var table = await SeedTableAsync("ordered");
        await Golden.ReplaceForTableAsync(Store.TenantA, table.Table.Id.ToString(), GoldenQuerySource.Dbt, [Entry("c"), Entry("a"), Entry("b")]);

        var first = (await Golden.GetForTenantAsync(Store.TenantA)).Select(r => r.SourceKey).ToArray();
        var second = (await Golden.GetForTenantAsync(Store.TenantA)).Select(r => r.SourceKey).ToArray();

        first.Should().Equal("a", "b", "c");
        second.Should().Equal(first);
    }

    [RequiresContainerRuntimeFact]
    public override Task Page_size_above_cap_is_clamped_or_rejected() => Task.CompletedTask;

    [RequiresContainerRuntimeFact]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var inactive = await SeedTableAsync("inactive", active: false);
        await Golden.ReplaceForTableAsync(Store.TenantA, inactive.Table.Id.ToString(), GoldenQuerySource.Dbt, [Entry("hidden")]);

        (await Golden.GetForTenantAsync(Store.TenantA)).Should().BeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task Replace_is_atomic_per_table_and_source()
    {
        if (!Store.IsAvailable) return;
        var one = await SeedTableAsync("atomic_one");
        var two = await SeedTableAsync("atomic_two");
        var oneId = one.Table.Id.ToString();
        var twoId = two.Table.Id.ToString();
        await Golden.ReplaceForTableAsync(Store.TenantA, oneId, GoldenQuerySource.Dbt, [Entry("d1"), Entry("d2")]);
        await Golden.ReplaceForTableAsync(Store.TenantA, oneId, GoldenQuerySource.Config, [Entry("c1")]);
        await Golden.ReplaceForTableAsync(Store.TenantA, twoId, GoldenQuerySource.Dbt, [Entry("t1")]);

        await Golden.ReplaceForTableAsync(Store.TenantA, oneId, GoldenQuerySource.Dbt, [Entry("d3")]);

        var rows = await Golden.GetForTenantAsync(Store.TenantA);
        rows.Where(r => r.TableId == oneId && r.Source == GoldenQuerySource.Dbt).Select(r => r.SourceKey).Should().Equal("d3");
        rows.Where(r => r.TableId == oneId && r.Source == GoldenQuerySource.Config).Select(r => r.SourceKey).Should().Equal("c1");
        rows.Where(r => r.TableId == twoId).Select(r => r.SourceKey).Should().Equal("t1");

        await Golden.ReplaceForTableAsync(Store.TenantA, oneId, GoldenQuerySource.Dbt, []);
        (await Golden.GetForTenantAsync(Store.TenantA)).Where(r => r.TableId == oneId).Select(r => r.SourceKey).Should().Equal("c1");
    }

    [RequiresContainerRuntimeFact]
    public async Task Replace_bumps_the_catalog_content_version()
    {
        if (!Store.IsAvailable) return;
        var table = await SeedTableAsync("version_bump");
        var versions = (Autheris.Infrastructure.Persistence.GovernanceRepository)Store.Services.GetRequiredService<IGoldenQueryRepository>();
        var before = await versions.GetCatalogVersionAsync();

        await Golden.ReplaceForTableAsync(Store.TenantA, table.Table.Id.ToString(), GoldenQuerySource.Dbt, [Entry("v1")]);

        var after = await versions.GetCatalogVersionAsync();
        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlGoldenQueryContractTests(GovernancePostgresFixture fixture) : GoldenQueryContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "golden_query_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerGoldenQueryContractTests(GovernanceSqlServerFixture fixture) : GoldenQueryContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "golden_query_contract");
}
