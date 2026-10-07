namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>
/// Tests for the single remaining <see cref="DataCatalogSyncService"/> (formerly the gateway core implementation,
/// moved to Autheris.Extensions/DataCatalog in EXT-MOVE): active client via <see cref="IDataCatalogClientFactory"/>,
/// GDPR Art. 9 tightening, masking via TagToMaskingRuleMap, governance ratchet, epoch invalidation and dry-run.
/// </summary>
public class DataCatalogSyncTests
{
    private readonly IDataCatalogClient _catalogClient = Substitute.For<IDataCatalogClient>();
    private readonly IDataCatalogClientFactory _clientFactory = Substitute.For<IDataCatalogClientFactory>();
    private readonly ITableMetadataRepository _tableRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IEpochValidationService _epochService = Substitute.For<IEpochValidationService>();
    private readonly DataCatalogSyncService _sut;

    public DataCatalogSyncTests()
    {
        _catalogClient.ProviderType.Returns(DataCatalogProviderType.MicrosoftPurview);
        _clientFactory.GetActiveClient().Returns(_catalogClient);

        var gatewayOptions = new GatewayOptions
        {
            Catalog = new DataCatalogOptions
            {
                Provider = DataCatalogProviderType.MicrosoftPurview,
                GdprArticle9Tags = ["gdpr_art9"],
                TagToMaskingRuleMap = new Dictionary<string, string>
                {
                    ["PII.Email"] = "MASK_EMAIL"
                }
            }
        };

        _sut = new DataCatalogSyncService(
            _clientFactory,
            _tableRepo,
            _epochService,
            Options.Create(gatewayOptions),
            NullLogger<DataCatalogSyncService>.Instance);
    }

    [Fact]
    public async Task SyncCatalogAsync_WhenGdprArticle9TagPresent_EnforcesHighSensitivityFourEyesAndMasking()
    {
        var tableId = new TableIdentifier("healthcare", "dbo", "patient_health_records");
        var catalogTable = new CatalogTableAsset
        {
            Identifier = tableId,
            DisplayName = "Patient Health Records",
            Description = "Sensitive clinical data",
            Tags = ["gdpr_art9", "clinical"],
            Columns =
            [
                new()
                {
                    ColumnName = "genetic_markers",
                    DataType = "varchar",
                    Tags = ["biometric"]
                },
                new()
                {
                    ColumnName = "patient_email",
                    DataType = "varchar",
                    Tags = ["PII.Email"]
                }
            ]
        };

        _catalogClient.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CatalogTableAsset> { catalogTable });

        var result = await _sut.SyncCatalogAsync(dryRun: false);

        result.Success.ShouldBeTrue();
        result.SyncedTablesCount.ShouldBe(1);
        result.Art9ProtectedTablesCount.ShouldBe(1);
        result.MaskedColumnsCount.ShouldBe(1);

        await _tableRepo.Received(1).UpsertTableMetadataAsync(
            Arg.Is<TableMetadata>(m =>
                m.Identifier.Equals(tableId) &&
                m.Table.Sensitivity == "HIGH" &&
                m.Table.RequiresFourEyes == true &&
                m.ColumnMaskingRules.ContainsKey("patient_email") &&
                m.ColumnMaskingRules["patient_email"].RuleType == "MASK_EMAIL"),
            Arg.Any<CancellationToken>());
        await _epochService.Received(1).InvalidateEpochAsync(Arg.Is<TableIdentifier>(t => t.Equals(tableId)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncCatalogAsync_Art9TagOnColumnOnly_RedactsColumnAndTightensTable(bool asClassification)
    {
        // POL-5: an Art. 9 tag on a single column (table without the tag) must protect that column and the table.
        var tableId = new TableIdentifier("healthcare", "dbo", "visits");
        var healthColumn = asClassification
            ? new CatalogColumnAsset { ColumnName = "diagnosis", DataType = "varchar", Classifications = ["gdpr_art9"] }
            : new CatalogColumnAsset { ColumnName = "diagnosis", DataType = "varchar", Tags = ["gdpr_art9"] };
        var catalogTable = new CatalogTableAsset
        {
            Identifier = tableId,
            Tags = ["clinical"],
            Columns =
            [
                new() { ColumnName = "visit_id", DataType = "int" },
                healthColumn
            ]
        };

        _catalogClient.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CatalogTableAsset> { catalogTable });

        var result = await _sut.SyncCatalogAsync(dryRun: false);

        result.Art9ProtectedTablesCount.ShouldBe(1);
        await _tableRepo.Received(1).UpsertTableMetadataAsync(
            Arg.Is<TableMetadata>(m =>
                m.Identifier.Equals(tableId) &&
                m.Table.Sensitivity == "HIGH" &&
                m.Table.RequiresFourEyes &&
                m.Columns.Single(c => c.ColumnName == "diagnosis").IsSensitive &&
                !m.Columns.Single(c => c.ColumnName == "visit_id").IsSensitive &&
                m.ColumnMaskingRules.ContainsKey("diagnosis") &&
                m.ColumnMaskingRules["diagnosis"].RuleType == "REDACT"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncCatalogAsync_DryRunMode_DoesNotPersistToRepository()
    {
        var tableId = new TableIdentifier("sales", "dbo", "customers");
        var catalogTable = new CatalogTableAsset
        {
            Identifier = tableId,
            Tags = ["crm"],
            Columns =
            [
                new() { ColumnName = "phone_number", Tags = ["phone"] }
            ]
        };

        _catalogClient.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CatalogTableAsset> { catalogTable });

        var result = await _sut.SyncCatalogAsync(dryRun: true);

        result.Success.ShouldBeTrue();
        result.SyncedTablesCount.ShouldBe(1);

        // Dry-run must never persist or invalidate epochs
        await _tableRepo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
        await _epochService.DidNotReceive().InvalidateEpochAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncCatalogAsync_ClientFailure_IsPropagated()
    {
        _catalogClient.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CatalogTableAsset>>>(_ => throw new InvalidOperationException("catalog down"));

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.SyncCatalogAsync(dryRun: false));
        await _tableRepo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
    }
}
