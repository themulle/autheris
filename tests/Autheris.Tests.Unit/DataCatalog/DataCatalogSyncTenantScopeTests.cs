namespace Autheris.Tests.Unit.DataCatalog;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

public sealed class DataCatalogSyncTenantScopeTests
{
    [Fact]
    public async Task SyncCatalog_Restricted_SkipsForeignAndUnknownTables_NoForeignNamesInWarnings()
    {
        var clientFactory = Substitute.For<IDataCatalogClientFactory>();
        var client = Substitute.For<IDataCatalogClient>();
        clientFactory.GetActiveClient().Returns(client);
        client.ProviderType.Returns(DataCatalogProviderType.OpenMetadata);

        var ownTableId = new TableIdentifier("finance", "dbo", "own_table");
        var foreignTableId = new TableIdentifier("finance", "dbo", "foreign_table");
        var unknownTableId = new TableIdentifier("finance", "dbo", "unknown_table");

        var tables = new List<CatalogTableAsset>
        {
            new() { Identifier = ownTableId },
            new() { Identifier = foreignTableId },
            new() { Identifier = unknownTableId }
        };
        client.GetTablesAsync(null, Arg.Any<CancellationToken>()).Returns(tables);

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var epochService = Substitute.For<IEpochValidationService>();

        var ownMeta = new TableMetadata
        {
            Identifier = ownTableId,
            OwnerTenantId = "tenant-a",
            Table = new Table { TableName = "own_table", IsActive = true }
        };
        var foreignMeta = new TableMetadata
        {
            Identifier = foreignTableId,
            OwnerTenantId = "tenant-b",
            Table = new Table { TableName = "foreign_table", IsActive = true }
        };

        metadataRepo.GetTableMetadataAsync(ownTableId, Arg.Any<CancellationToken>()).Returns(ownMeta);
        metadataRepo.GetTableMetadataAsync(foreignTableId, Arg.Any<CancellationToken>()).Returns(foreignMeta);
        metadataRepo.GetTableMetadataAsync(unknownTableId, Arg.Any<CancellationToken>()).Returns((TableMetadata?)null);

        var options = Options.Create(new GatewayOptions());
        var sut = new DataCatalogSyncService(
            clientFactory,
            metadataRepo,
            epochService,
            options,
            NullLogger<DataCatalogSyncService>.Instance);

        var result = await sut.SyncCatalogAsync(dryRun: false, restrictToTenant: new TenantId("tenant-a"));

        result.SyncedTablesCount.ShouldBe(1);
        result.AffectedTables.ShouldContain(ownTableId);
        result.AffectedTables.ShouldNotContain(foreignTableId);
        result.AffectedTables.ShouldNotContain(unknownTableId);

        foreach (var warning in result.Warnings)
        {
            warning.ShouldNotContain("foreign_table");
            warning.ShouldNotContain("unknown_table");
        }
    }
}
