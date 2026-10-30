namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using TargetSqlDialect = TrinoSqlEngine.TargetSqlDialect;

/// <summary>Issue #404 WP-404-2: the catalog sync persists the physical non-unique indexes (D-5) with a minimal audit, fail closed.</summary>
[Trait("Category", "Security")]
public sealed class Issue404DataCatalogSyncPhysicalIndexTests
{
    private const KeyColumnDirection A = KeyColumnDirection.Ascending;
    private const KeyColumnDirection D = KeyColumnDirection.Descending;
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private sealed class CollectingLogger : ILogger<DataCatalogSyncService>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : "|EX:" + exception.Message));
    }

    private readonly IDataCatalogClient _client = Substitute.For<IDataCatalogClient>();
    private readonly ITableMetadataRepository _repo = Substitute.For<ITableMetadataRepository>();
    private readonly IEpochValidationService _epoch = Substitute.For<IEpochValidationService>();
    private readonly IPhysicalKeyCatalog _physical = Substitute.For<IPhysicalKeyCatalog>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IPhysicalIndexRepository _indexes = Substitute.For<IPhysicalIndexRepository>();
    private readonly CollectingLogger _log = new();
    private readonly List<string> _calls = new();
    private readonly List<AuditLogEntry> _audits = new();
    private readonly List<IReadOnlyList<PhysicalIndexDefinition>> _replaced = new();

    public Issue404DataCatalogSyncPhysicalIndexTests()
    {
        _client.ProviderType.Returns(DataCatalogProviderType.MicrosoftPurview);
        _repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            _calls.Add("upsert");
            return Task.FromResult(c.Arg<TableMetadata>());
        });
        _audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            _calls.Add("audit");
            _audits.Add(c.Arg<AuditLogEntry>());
            return Task.CompletedTask;
        });
        _epoch.InvalidateEpochAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _calls.Add("epoch");
            return Task.CompletedTask;
        });
        _indexes.ReplacePhysicalIndexesAsync(Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<PhysicalIndexDefinition>>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            _calls.Add("replace");
            _replaced.Add(c.Arg<IReadOnlyList<PhysicalIndexDefinition>>());
            return Task.FromResult(true);
        });
    }

    private DataCatalogSyncService Sut()
    {
        var factory = Substitute.For<IDataCatalogClientFactory>();
        factory.GetActiveClient().Returns(_client);
        return new DataCatalogSyncService(factory, _repo, _epoch,
            Options.Create(new GatewayOptions { Catalog = new DataCatalogOptions { Provider = DataCatalogProviderType.MicrosoftPurview } }),
            _physical, _audit, _log, physicalIndexes: _indexes);
    }

    private void Physical(PhysicalKeySnapshot snapshot) =>
        _physical.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<PhysicalKeySnapshot>(snapshot));

    private static PhysicalKeySnapshot Known(params PhysicalNonUniqueIndex[] nonUnique) =>
        new(PhysicalKeySnapshotState.Known, [new PhysicalKey(true, true, ["id"], [A])], TargetSqlDialect.SqlServer,
            new PhysicalNonUniqueSnapshot(PhysicalKeySnapshotState.Known, nonUnique));

    private static PhysicalNonUniqueIndex Idx(string[] columns, params KeyColumnDirection[] directions) =>
        new(columns, directions.Length == 0 ? columns.Select(_ => A).ToArray() : directions);

    private void Catalog(TableMetadata? existing = null)
    {
        var asset = new CatalogTableAsset
        {
            Identifier = Orders,
            SourceType = "SqlServer",
            PrimaryKeyColumns = ["id"],
            Columns = new[] { "id", "ts", "secret_col" }.Select(c => new CatalogColumnAsset { ColumnName = c, DataType = "int" }).ToList()
        };
        _client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<CatalogTableAsset> { asset });
        _repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(existing);
    }

    private static TableMetadata Stored(IReadOnlyList<PhysicalIndexDefinition> physical, string? owner = null) => new()
    {
        Identifier = Orders,
        OwnerTenantId = owner,
        Table = new Table { SourceName = "ds", SourceType = "SqlServer", DataSourceType = DataSourceType.Sql, Sensitivity = "NORMAL" },
        PrimaryKeyColumns = ["id"],
        PhysicalIndexes = physical
    };

    [Fact]
    public async Task I404_T_18_Sync_Known_ReplacesOnlyWhenChanged()
    {
        Catalog();
        Physical(Known(Idx(["ts"]), Idx(["secret_col", "ts"], A, D)));

        var first = await Sut().SyncCatalogAsync();

        first.Warnings.ShouldBeEmpty();
        _replaced.Single().Select(i => string.Join(",", i.Columns)).ToArray().ShouldBe(["ts", "secret_col,ts"]);
        _replaced.Single()[1].Directions.ShouldBe([A, D]);

        _calls.Clear();
        _replaced.Clear();
        Catalog(Stored(new[] { new PhysicalIndexDefinition(["ts"], []), new PhysicalIndexDefinition(["secret_col", "ts"], [A, D]) }));
        await Sut().SyncCatalogAsync();
        _replaced.ShouldBeEmpty("an unchanged list is not written");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I404-SC-03")]
    public async Task I404_T_S03_Sync_Replace_WritesAudit_BeforeStore_NoNamesNoColumns()
    {
        Catalog(Stored([new PhysicalIndexDefinition(["ts"], [])], owner: "tenant-a"));
        Physical(Known(Idx(["ts"]), Idx(["secret_col"])));

        await Sut().SyncCatalogAsync();

        _calls.ShouldBe(["upsert", "audit", "replace", "epoch"], "audit strictly before the replace, replace before the epoch invalidation (I404-SC-14)");
        var entry = _audits.Single();
        entry.EventType.ShouldBe(AuditEventTypes.CatalogPhysicalIndexesChanged);
        entry.ActorSid.Value.ShouldBe("system:catalog-sync");
        entry.TenantId.ShouldBe(new TenantId("tenant-a"));
        entry.DetailsJson.ShouldContain("\"beforeCount\":1");
        entry.DetailsJson.ShouldContain("\"afterCount\":2");
        entry.DetailsJson.ShouldNotContain("secret_col");
        entry.DetailsJson.ShouldNotContain("\"ts\"");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I404-SC-03")]
    public async Task I404_T_S03_Sync_AuditFailure_KeepsStoredList()
    {
        foreach (Exception failure in new Exception[] { new InvalidOperationException("SENTINEL-AUDIT"), new OperationCanceledException() })
        {
            _calls.Clear();
            _replaced.Clear();
            Catalog(Stored([new PhysicalIndexDefinition(["ts"], [])]));
            Physical(Known(Idx(["secret_col"])));
            _audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException(failure));

            var result = await Sut().SyncCatalogAsync();

            _replaced.ShouldBeEmpty();
            result.Warnings.ShouldBe(["Table 'sales.dbo.orders': PHYSICAL_INDEX_AUDIT_FAILED"]);
            _log.Messages.ShouldNotContain(m => m.Contains("SENTINEL-AUDIT") || m.Contains("secret_col"));
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I404-SC-03")]
    public async Task I404_T_S04_Sync_NoChange_WritesNoAudit()
    {
        Catalog(Stored([new PhysicalIndexDefinition(["ts"], [])]));
        Physical(Known(Idx(["TS"])));

        await Sut().SyncCatalogAsync();

        _audits.ShouldBeEmpty();
        _replaced.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I404-SC-04")]
    public async Task I404_T_S02_Sync_Unavailable_KeepsStored_WarningHasCodeAndTableIdOnly_NoIndexNameNoErrorText()
    {
        Catalog(Stored([new PhysicalIndexDefinition(["secret_col"], [])]));
        var unavailable = new PhysicalKeySnapshot(PhysicalKeySnapshotState.Known, [new PhysicalKey(true, true, ["id"], [A])], TargetSqlDialect.SqlServer,
            new PhysicalNonUniqueSnapshot(PhysicalKeySnapshotState.Unavailable));
        Physical(unavailable);

        var result = await Sut().SyncCatalogAsync();

        result.Warnings.ShouldBe(["Table 'sales.dbo.orders': PHYSICAL_INDEX_UNAVAILABLE"]);
        _replaced.ShouldBeEmpty();
        _audits.ShouldBeEmpty();

        // A write failure keeps the stored list as well and reports the type only.
        _indexes.ReplacePhysicalIndexesAsync(default!, default!, default).ReturnsForAnyArgs(_ => Task.FromException<bool>(new InvalidOperationException("SENTINEL-WRITE secret_col")));
        Physical(Known(Idx(["ts"])));
        var failed = await Sut().SyncCatalogAsync();
        failed.Warnings.ShouldBe(["Table 'sales.dbo.orders': PHYSICAL_INDEX_UNAVAILABLE"]);
        _log.Messages.ShouldNotContain(m => m.Contains("SENTINEL-WRITE") || m.Contains("secret_col"));
    }

    [Fact]
    public async Task I404_T_20_Sync_Unsupported_ClearsStored()
    {
        Catalog(Stored([new PhysicalIndexDefinition(["ts"], [])]));
        Physical(PhysicalKeySnapshot.Unsupported);

        await Sut().SyncCatalogAsync();

        _replaced.Single().ShouldBeEmpty();
        _audits.Single().DetailsJson.ShouldContain("\"afterCount\":0");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I404-SC-08")]
    public async Task I404_T_21_Sync_DryRun_WritesNothing()
    {
        Catalog(Stored([new PhysicalIndexDefinition(["ts"], [])]));
        Physical(Known(Idx(["secret_col"])));

        await Sut().SyncCatalogAsync(dryRun: true);

        _calls.ShouldBeEmpty();
        await _indexes.DidNotReceiveWithAnyArgs().ReplacePhysicalIndexesAsync(default!, default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I404-SC-07")]
    public async Task I404_T_22_Sync_AdmissionDenied_NoReadNoWrite()
    {
        Catalog(Stored([], owner: "tenant-b"));
        Physical(Known(Idx(["ts"])));

        var result = await Sut().SyncCatalogAsync(restrictToTenant: new TenantId("tenant-a"));

        result.Warnings.ShouldBeEmpty();
        await _physical.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
        await _indexes.DidNotReceiveWithAnyArgs().ReplacePhysicalIndexesAsync(default!, default!, default);
    }
}
