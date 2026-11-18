namespace Autheris.Tests.Unit.Governance.Catalog;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed class TableEpochBatchReadTests
{
    private static int EpochStatements(CatalogPagedReadSecurityTests.FakeCatalogStore store) =>
        store.RecordedCommands.Count(c => c.Sql.Contains("FROM POLICY_EPOCHS", StringComparison.OrdinalIgnoreCase));

    private static int EntityLoads(CatalogPagedReadSecurityTests.FakeCatalogStore store) =>
        store.RecordedCommands.Count(c => c.Sql.Contains("FROM TABLES", StringComparison.OrdinalIgnoreCase)
            && c.Sql.Contains("LOWER(table_name) = LOWER(@table)", StringComparison.OrdinalIgnoreCase));

    private static (CatalogPagedReadSecurityTests.FakeCatalogStore Store, CatalogPagedReadSecurityTests.FakeTableRecord[] Records, Autheris.Infrastructure.Persistence.GovernanceRepository Repo) Create(int count = 5)
    {
        var records = Enumerable.Range(0, count)
            .Select(i => CatalogPagedReadSecurityTests.MakeRecord("sales", "public", $"tbl_{i}", $"initial_{i}", epoch: 1))
            .ToArray();
        var store = new CatalogPagedReadSecurityTests.FakeCatalogStore(records);
        return (store, records, CatalogPagedReadSecurityTests.CreateRepository(store, postgresql: true));
    }

    [Fact]
    public async Task QueryOverFiveTables_OneEpochRoundTrip()
    {
        var (store, records, repo) = Create();
        var ids = records.Select(r => r.Identifier).ToList();

        var first = await repo.GetTableMetadataBatchAsync(ids);
        first.Count.ShouldBe(5);
        first.ShouldAllBe(t => t != null);

        store.RecordedCommands.Clear();
        var second = await repo.GetTableMetadataBatchAsync(ids);

        second.Select(t => t!.Identifier).ShouldBe(ids);
        EpochStatements(store).ShouldBe(1);
        EntityLoads(store).ShouldBe(0);
    }

    [Fact]
    public async Task UnknownIdentifier_ResolvesToNullInRequestOrder()
    {
        var (_, records, repo) = Create(2);
        var unknown = new TableIdentifier("sales", "public", "does_not_exist");

        var result = await repo.GetTableMetadataBatchAsync([records[1].Identifier, unknown, records[0].Identifier]);

        result.Count.ShouldBe(3);
        result[0]!.Identifier.ShouldBe(records[1].Identifier);
        result[1].ShouldBeNull();
        result[2]!.Identifier.ShouldBe(records[0].Identifier);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task MissingEpochRow_IsMissNotHit()
    {
        var (store, records, repo) = Create();
        var ids = records.Select(r => r.Identifier).ToList();
        await repo.GetTableMetadataBatchAsync(ids);

        // The epoch row of one table disappears while its data changes: the cached entry must never be served.
        store.MissingEpochRows.Add(records[2].Id);
        store.UpdateDescription(records[2].Id, "changed_2", newEpoch: 1);
        store.RecordedCommands.Clear();

        var result = await repo.GetTableMetadataBatchAsync(ids);

        result[2]!.Table.Description.ShouldBe("changed_2");
        result[0]!.Table.Description.ShouldBe("initial_0");
        EntityLoads(store).ShouldBe(1);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ReadFailure_FailsClosed()
    {
        var (store, records, repo) = Create();
        var ids = records.Select(r => r.Identifier).ToList();
        await repo.GetTableMetadataBatchAsync(ids);

        store.FailEpochReads = true;
        store.UpdateDescription(records[0].Id, "changed_0", newEpoch: 1);
        store.RecordedCommands.Clear();

        var result = await repo.GetTableMetadataBatchAsync(ids);

        result[0]!.Table.Description.ShouldBe("changed_0");
        EntityLoads(store).ShouldBe(5);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task EpochChange_ReloadsOnlyTheChangedTable()
    {
        var (store, records, repo) = Create();
        var ids = records.Select(r => r.Identifier).ToList();
        await repo.GetTableMetadataBatchAsync(ids);

        store.UpdateDescription(records[4].Id, "changed_4", newEpoch: 2);
        store.RecordedCommands.Clear();

        var result = await repo.GetTableMetadataBatchAsync(ids);

        result[4]!.Table.Description.ShouldBe("changed_4");
        EntityLoads(store).ShouldBe(1);
        EpochStatements(store).ShouldBeGreaterThanOrEqualTo(1);
    }
}
