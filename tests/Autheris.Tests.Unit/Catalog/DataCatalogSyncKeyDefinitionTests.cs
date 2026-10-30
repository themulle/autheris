namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Sql.Catalog;
using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #94 (D-8): data catalog sync maps declared keys; IsPrimaryKey flags become the declared primary key.</summary>
public sealed class DataCatalogSyncKeyDefinitionTests
{
    private static readonly TableIdentifier Id = new("crm", "dbo", "orders");
    private readonly ITableMetadataRepository _repo = Substitute.For<ITableMetadataRepository>();
    private readonly IDataCatalogClient _client = Substitute.For<IDataCatalogClient>();
    private readonly List<TableMetadata> _upserts = new();

    private DataCatalogSyncService Sut()
    {
        var factory = Substitute.For<IDataCatalogClientFactory>();
        _client.ProviderType.Returns(DataCatalogProviderType.MicrosoftPurview);
        factory.GetActiveClient().Returns(_client);
        _repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(c => { _upserts.Add(c.Arg<TableMetadata>()); return Task.FromResult(c.Arg<TableMetadata>()); });
        return new DataCatalogSyncService(factory, _repo, Substitute.For<IEpochValidationService>(),
            Options.Create(new GatewayOptions { Catalog = new DataCatalogOptions { Provider = DataCatalogProviderType.MicrosoftPurview } }),
            Substitute.For<IPhysicalKeyCatalog>(), Substitute.For<IAuditWriter>(), NullLogger<DataCatalogSyncService>.Instance);
    }

    private static CatalogTableAsset Asset(Action<CatalogTableAsset, List<CatalogColumnAsset>>? configure = null)
    {
        var cols = new List<CatalogColumnAsset>
        {
            new() { ColumnName = "tenant", DataType = "int" },
            new() { ColumnName = "id", DataType = "int" },
            new() { ColumnName = "email", DataType = "varchar" }
        };
        var asset = new CatalogTableAsset { Identifier = Id, Columns = cols };
        configure?.Invoke(asset, cols);
        return asset;
    }

    private async Task<TableMetadata> SyncAsync(CatalogTableAsset asset)
    {
        _client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<CatalogTableAsset> { asset });
        await Sut().SyncCatalogAsync(dryRun: false);
        return _upserts.Single();
    }

    [Fact]
    public async Task IsPrimaryKeyFlags_BecomeDeclaredPrimaryKey_InColumnOrder()
    {
        var asset = Asset();
        asset = asset with { Columns = [asset.Columns[0] with { IsPrimaryKey = true }, asset.Columns[1] with { IsPrimaryKey = true }, asset.Columns[2]] };

        var meta = await SyncAsync(asset);

        meta.ReplaceKeyDefinition.ShouldBeTrue();
        meta.PrimaryKeyColumns.ShouldBe(["tenant", "id"]);
    }

    [Fact]
    public async Task ExplicitPrimaryKeyAndIndexes_WinOverFlags()
    {
        var asset = Asset() with
        {
            PrimaryKeyColumns = ["id"],
            Indexes = [new TableIndexDefinition("ux_email", ["email"], true)]
        };
        asset = asset with { Columns = [asset.Columns[0] with { IsPrimaryKey = true }, asset.Columns[1], asset.Columns[2]] };

        var meta = await SyncAsync(asset);

        meta.ReplaceKeyDefinition.ShouldBeTrue();
        meta.PrimaryKeyColumns.ShouldBe(["id"]);
        meta.Indexes.Single().Name.ShouldBe("ux_email");
    }

    [Fact]
    public async Task NothingDeclared_DoesNotReplace()
    {
        var meta = await SyncAsync(Asset());
        meta.ReplaceKeyDefinition.ShouldBeFalse();
        meta.PrimaryKeyColumns.ShouldBeEmpty();
    }

    [Fact]
    public async Task NothingDeclared_KeepsExistingDefinition_ThroughTheRatchet()
    {
        _repo.GetTableMetadataAsync(Id, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Identifier = Id,
            Table = new Table { SourceName = "crm", SchemaName = "dbo", TableName = "orders", SourceType = "PostgreSQL", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id" }],
            PrimaryKeyColumns = ["id"]
        });

        var meta = await SyncAsync(Asset());

        meta.ReplaceKeyDefinition.ShouldBeFalse();
        meta.PrimaryKeyColumns.ShouldBe(["id"]);
    }

    [Fact]
    public async Task UnknownKeyColumn_DefinitionDropped_TableIngested()
    {
        var meta = await SyncAsync(Asset() with { PrimaryKeyColumns = ["ghost"] });
        meta.ReplaceKeyDefinition.ShouldBeFalse();
        meta.PrimaryKeyColumns.ShouldBeEmpty();
        meta.Columns.Count.ShouldBe(3);
    }

    [Fact]
    public async Task OversizedKey_DefinitionDropped()
    {
        var meta = await SyncAsync(Asset() with { PrimaryKeyColumns = Enumerable.Range(0, 100).Select(i => $"c{i}").ToList() });
        meta.ReplaceKeyDefinition.ShouldBeFalse();
    }
}
