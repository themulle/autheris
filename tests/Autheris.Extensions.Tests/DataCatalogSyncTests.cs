namespace Autheris.Extensions.Tests;

using Autheris.Application.Sql.Catalog;
using Autheris.Application.Audit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Models;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Tests for <see cref="DataCatalogSyncService"/>:
/// GDPR Art. 9 tightening, masking via TagToMaskingRuleMap, governance ratchet, epoch invalidation and dry-run.
/// </summary>
public class DataCatalogSyncTests
{
    private readonly StubDataCatalogClient _catalogClient = new(DataCatalogProviderType.OpenMetadata);
    private readonly InMemoryTableMetadataRepository _tableRepo = new();
    private readonly FakeEpochValidationService _epochService = new();
    private readonly DataCatalogSyncService _sut;

    public DataCatalogSyncTests()
    {
        var gatewayOptions = new GatewayOptions
        {
            Catalog = new DataCatalogOptions
            {
                Provider = DataCatalogProviderType.OpenMetadata,
                GdprArticle9Tags = ["gdpr_art9"],
                TagToMaskingRuleMap = new Dictionary<string, string>
                {
                    ["PII.Email"] = "MASK_EMAIL"
                }
            }
        };

        _sut = new DataCatalogSyncService(
            _catalogClient,
            _tableRepo,
            _epochService,
            Options.Create(gatewayOptions),
            new InMemoryAuditWriter(), NullLogger<DataCatalogSyncService>.Instance);
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

        _catalogClient.Tables = [catalogTable];

        var result = await _sut.SyncCatalogAsync(dryRun: false);

        result.Success.ShouldBeTrue();
        result.SyncedTablesCount.ShouldBe(1);
        result.Art9ProtectedTablesCount.ShouldBe(1);
        result.MaskedColumnsCount.ShouldBe(1);

        var upserted = _tableRepo.UpsertedTables.ShouldHaveSingleItem();
        upserted.Identifier.ShouldBe(tableId);
        upserted.Table.Sensitivity.ShouldBe("HIGH");
        upserted.Table.RequiresFourEyes.ShouldBeTrue();
        upserted.ColumnMaskingRules.ContainsKey("patient_email").ShouldBeTrue();
        upserted.ColumnMaskingRules["patient_email"].RuleType.ShouldBe("MASK_EMAIL");
        _epochService.InvalidatedTables.Count(t => t.Equals(tableId)).ShouldBe(1);
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

        _catalogClient.Tables = [catalogTable];

        var result = await _sut.SyncCatalogAsync(dryRun: false);

        result.Art9ProtectedTablesCount.ShouldBe(1);
        var m = _tableRepo.UpsertedTables.ShouldHaveSingleItem();
        m.Identifier.ShouldBe(tableId);
        m.Table.Sensitivity.ShouldBe("HIGH");
        m.Table.RequiresFourEyes.ShouldBeTrue();
        ClassificationMetadata.IsColumnSensitive(m, "diagnosis").ShouldBeTrue();
        ClassificationMetadata.IsColumnSensitive(m, "visit_id").ShouldBeFalse();
        m.ColumnMaskingRules.ContainsKey("diagnosis").ShouldBeTrue();
        m.ColumnMaskingRules["diagnosis"].RuleType.ShouldBe("REDACT");
    }

    [Fact]
    public async Task EXT_2_SyncCatalogAsync_KeepsThePersistedSourceName()
    {
        var tableId = new TableIdentifier("sales", "dbo", "invoices");
        _catalogClient.Tables = [new CatalogTableAsset { Identifier = tableId }];
        _tableRepo.Add(new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "sales_readonly_conn", SchemaName = "dbo", TableName = "invoices", IsActive = true }
        });

        await _sut.SyncCatalogAsync();

        var m = _tableRepo.UpsertedTables.ShouldHaveSingleItem();
        m.Table.SourceName.ShouldBe("sales_readonly_conn");
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

        _catalogClient.Tables = [catalogTable];

        var result = await _sut.SyncCatalogAsync(dryRun: true);

        result.Success.ShouldBeTrue();
        result.SyncedTablesCount.ShouldBe(1);

        // Dry-run must never persist or invalidate epochs
        _tableRepo.UpsertCalls.ShouldBe(0);
        _epochService.InvalidatedTables.ShouldBeEmpty();
    }

    [Fact]
    public async Task SEC_SYNC_EPOCH_SyncCatalogAsync_FailureMidRun_StillInvalidatesTablesAlreadyWritten()
    {
        var first = new TableIdentifier("sales", "dbo", "first");
        var failing = new TableIdentifier("sales", "dbo", "failing");
        var never = new TableIdentifier("sales", "dbo", "never");

        _catalogClient.Tables =
        [
            new() { Identifier = first },
            new() { Identifier = failing },
            new() { Identifier = never }
        ];
        _tableRepo.SimulateEmptyBatchResult = true;
        _tableRepo.OnUpsert = m =>
        {
            if (m.Identifier.Equals(failing))
            {
                throw new InvalidOperationException("database down");
            }
        };

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.SyncCatalogAsync(dryRun: false));

        // The table written before the failure (and the one whose write failed part-way) no longer serve stale decisions.
        _epochService.InvalidatedTables.Count(t => t.Equals(first)).ShouldBe(1);
        _epochService.InvalidatedTables.Count(t => t.Equals(failing)).ShouldBe(1);
        _epochService.InvalidatedTables.ShouldNotContain(t => t.Equals(never));
    }

    [Fact]
    public async Task SyncCatalogAsync_ClientFailure_IsPropagated()
    {
        _catalogClient.OnGetTables = () => throw new InvalidOperationException("catalog down");

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.SyncCatalogAsync(dryRun: false));
        _tableRepo.UpsertCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EXT_7_SyncCatalogAsync_NewTableWithoutActivateNewTables_LeavesTableInactive()
    {
        var tableId = new TableIdentifier("sales", "dbo", "leads");
        var catalogTable = new CatalogTableAsset
        {
            Identifier = tableId,
            DisplayName = "Leads",
            Tags = [],
            Columns = [new() { ColumnName = "id", DataType = "int" }]
        };

        _catalogClient.Tables = [catalogTable];

        var result = await _sut.SyncCatalogAsync(dryRun: false);

        result.Success.ShouldBeTrue();
        var m = _tableRepo.UpsertedTables.ShouldHaveSingleItem();
        m.Identifier.ShouldBe(tableId);
        m.Table.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task EXT_7_SyncCatalogAsync_WhenActivateNewTablesIsTrue_ActivatesTable()
    {
        var gatewayOptions = new GatewayOptions
        {
            Catalog = new DataCatalogOptions
            {
                Provider = DataCatalogProviderType.OpenMetadata,
                ActivateNewTables = true
            }
        };

        var sut = new DataCatalogSyncService(
            _catalogClient,
            _tableRepo,
            _epochService,
            Options.Create(gatewayOptions),
            new InMemoryAuditWriter(), NullLogger<DataCatalogSyncService>.Instance);

        var tableId = new TableIdentifier("sales", "dbo", "leads_active");
        var catalogTable = new CatalogTableAsset
        {
            Identifier = tableId,
            DisplayName = "Leads Active",
            Tags = [],
            Columns = [new() { ColumnName = "id", DataType = "int" }]
        };

        _catalogClient.Tables = [catalogTable];

        var result = await sut.SyncCatalogAsync(dryRun: false);

        result.Success.ShouldBeTrue();
        var m = _tableRepo.UpsertedTables.ShouldHaveSingleItem();
        m.Identifier.ShouldBe(tableId);
        m.Table.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task DataCatalogSync_KeepsRequiredFilterColumns()
    {
        var tableId = new TableIdentifier("corp", "hr", "employees");
        var existingTable = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceName = "corp",
                SchemaName = "hr",
                TableName = "employees",
                DisplayName = "Employees",
                RequiredFilterColumns = System.Collections.Immutable.ImmutableArray.Create("tenant_id")
            },
            Columns = [new() { ColumnName = "id", DataType = "int" }, new() { ColumnName = "tenant_id", DataType = "varchar" }]
        };

        var catalogTable = new CatalogTableAsset
        {
            Identifier = tableId,
            DisplayName = "Employees Updated",
            Description = "Updated description from catalog",
            Tags = [],
            Columns = [new() { ColumnName = "id", DataType = "int" }, new() { ColumnName = "tenant_id", DataType = "varchar" }]
        };

        _catalogClient.Tables = [catalogTable];
        _tableRepo.Add(existingTable);

        var result = await _sut.SyncCatalogAsync(dryRun: false);

        result.Success.ShouldBeTrue();
        var savedMeta = _tableRepo.UpsertedTables.ShouldHaveSingleItem();
        savedMeta.RequiredFilterColumns.ShouldContain("tenant_id");
        savedMeta.Table.Description.ShouldBe("Updated description from catalog");
    }
}
