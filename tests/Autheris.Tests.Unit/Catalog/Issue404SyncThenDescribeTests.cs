namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Audit;
using Autheris.Application.Data.Services;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using TargetSqlDialect = TrinoSqlEngine.TargetSqlDialect;

/// <summary>
/// Issue #404 T-24 (SQLite): sync with a faked physical reader, the real store write, the metadata read after the epoch invalidation and
/// the describe projection. Also pins that a removed and re-registered table cannot resurface old physical rows (I404 Phase 5).
/// </summary>
[Trait("Category", "Security")]
public sealed class Issue404SyncThenDescribeTests : IDisposable
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private readonly SqliteGovernanceRepository _repo;
    private readonly IEpochValidationService _epoch = Substitute.For<IEpochValidationService>();
    private readonly IDataCatalogClient _client = Substitute.For<IDataCatalogClient>();
    private readonly IPhysicalKeyCatalog _physical = Substitute.For<IPhysicalKeyCatalog>();
    private readonly List<string> _audits = new();

    public Issue404SyncThenDescribeTests()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=t24_{Guid.NewGuid():N};Mode=Memory;Cache=Shared", JournalMode = "DELETE" }
        });
        _repo = GovernanceStoreFactory.CreateSqlite(_epoch, options);
        _client.ProviderType.Returns(DataCatalogProviderType.MicrosoftPurview);
        _client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<CatalogTableAsset>
        {
            new()
            {
                Identifier = Orders,
                SourceType = "SqlServer",
                PrimaryKeyColumns = ["id"],
                Columns = new[] { "id", "ts", "secret" }.Select(c => new CatalogColumnAsset { ColumnName = c, DataType = "int" }).ToList()
            }
        });
        _physical.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<PhysicalKeySnapshot>(
            new PhysicalKeySnapshot(PhysicalKeySnapshotState.Known, [new PhysicalKey(true, true, ["id"], [KeyColumnDirection.Ascending])], TargetSqlDialect.SqlServer,
                new PhysicalNonUniqueSnapshot(PhysicalKeySnapshotState.Known,
                [
                    new PhysicalNonUniqueIndex(["ts", "id"], [KeyColumnDirection.Descending, KeyColumnDirection.Ascending]),
                    new PhysicalNonUniqueIndex(["secret"], [KeyColumnDirection.Ascending])
                ]))));
    }

    public void Dispose() => _repo.Dispose();

    private async Task SyncAsync()
    {
        var factory = Substitute.For<IDataCatalogClientFactory>();
        factory.GetActiveClient().Returns(_client);
        var audit = Substitute.For<IAuditWriter>();
        audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            _audits.Add(c.Arg<AuditLogEntry>().DetailsJson ?? string.Empty);
            return Task.CompletedTask;
        });
        var sut = new DataCatalogSyncService(factory, _repo, _epoch,
            Options.Create(new GatewayOptions { Catalog = new DataCatalogOptions { Provider = DataCatalogProviderType.MicrosoftPurview } }),
            _physical, audit, NullLogger<DataCatalogSyncService>.Instance, physicalIndexes: _repo);
        var result = await sut.SyncCatalogAsync();
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task I404_T_24_SyncThenDescribe_NonUniqueIndexListed_NoNames()
    {
        await SyncAsync();
        await _epoch.Received().InvalidateEpochAsync(Orders, Arg.Any<CancellationToken>());
        _repo.InvalidateMetadataCacheForTesting();

        var meta = (await _repo.GetTableMetadataAsync(Orders))!;
        meta.PhysicalIndexes.Count.ShouldBe(2);
        var clearAll = new HashSet<string>(["id", "ts", "secret"], StringComparer.OrdinalIgnoreCase);
        var view = DatasetIndexProjection.Project(meta, clearAll);

        view.Indexes.Select(i => string.Join(",", i.Columns)).ToArray().ShouldBe(["ts,id", "secret"]);
        view.Indexes.ShouldAllBe(i => !i.Unique);
        view.Indexes[0].Directions.ShouldBe([KeyColumnDirection.Descending, KeyColumnDirection.Ascending]);
        System.Text.Json.JsonSerializer.Serialize(view.Indexes).ShouldNotContain("name", Case.Insensitive);
        DatasetIndexProjection.Project(meta, new HashSet<string>(["id", "ts"], StringComparer.OrdinalIgnoreCase)).Indexes
            .Select(i => string.Join(",", i.Columns)).ToArray().ShouldBe(["ts,id"], "an index over a column that is not clear is omitted whole");
        _audits.ShouldHaveSingleItem().ShouldNotContain("secret");
    }

    [Fact]
    public async Task I404_RemovedAndReRegisteredTable_DoesNotResurfaceOldPhysicalRows()
    {
        await SyncAsync();
        using (var cmd = _repo.RawConnection().CreateCommand())
        {
            cmd.CommandText = "DELETE FROM TABLES WHERE table_name = 'orders'";
            cmd.ExecuteNonQuery();
        }

        _repo.InvalidateMetadataCacheForTesting();
        await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { SourceType = "sql", SourceName = "sales", SchemaName = "dbo", TableName = "orders", DisplayName = "orders" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "ts", DataType = "int" }]
        });
        _repo.InvalidateMetadataCacheForTesting();

        (await _repo.GetTableMetadataAsync(Orders))!.PhysicalIndexes.ShouldBeEmpty("a re-registered table gets a new id; old rows are keyed by the old id");
    }
}
