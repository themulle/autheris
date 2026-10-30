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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using TargetSqlDialect = TrinoSqlEngine.TargetSqlDialect;

/// <summary>I403 WP-403-3: the catalog sync reads key and index column direction from the source catalog (D-6).</summary>
public sealed class Issue403DataCatalogSyncDirectionTests
{
    private const KeyColumnDirection A = KeyColumnDirection.Ascending;
    private const KeyColumnDirection D = KeyColumnDirection.Descending;
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private readonly IDataCatalogClient _client = Substitute.For<IDataCatalogClient>();
    private readonly ITableMetadataRepository _repo = Substitute.For<ITableMetadataRepository>();
    private readonly IEpochValidationService _epoch = Substitute.For<IEpochValidationService>();
    private readonly IPhysicalKeyCatalog _physical = Substitute.For<IPhysicalKeyCatalog>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly List<string> _calls = new();
    private readonly List<TableMetadata> _upserts = new();
    private readonly List<AuditLogEntry> _audits = new();

    public Issue403DataCatalogSyncDirectionTests()
    {
        _client.ProviderType.Returns(DataCatalogProviderType.MicrosoftPurview);
        _repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            _calls.Add("upsert");
            _upserts.Add(c.Arg<TableMetadata>());
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
    }

    private DataCatalogSyncService Sut()
    {
        var factory = Substitute.For<IDataCatalogClientFactory>();
        factory.GetActiveClient().Returns(_client);
        return new DataCatalogSyncService(factory, _repo, _epoch,
            Options.Create(new GatewayOptions { Catalog = new DataCatalogOptions { Provider = DataCatalogProviderType.MicrosoftPurview } }),
            _physical, _audit, NullLogger<DataCatalogSyncService>.Instance);
    }

    private void Physical(PhysicalKeySnapshot snapshot) =>
        _physical.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<PhysicalKeySnapshot>(snapshot));

    private static PhysicalKeySnapshot Known(params PhysicalKey[] keys) => new(PhysicalKeySnapshotState.Known, keys, TargetSqlDialect.SqlServer);

    private static PhysicalKey Pk(string[] columns, params KeyColumnDirection[] directions) => new(true, true, columns, directions);

    private void Catalog(string[]? primaryKey = null, TableMetadata? existing = null, string sourceType = "SqlServer", bool flagPrimaryKey = false)
    {
        var asset = new CatalogTableAsset
        {
            Identifier = Orders,
            SourceType = sourceType,
            PrimaryKeyColumns = primaryKey?.ToList() ?? [],
            Columns = (primaryKey is { Length: > 0 } ? primaryKey : ["id", "ts"])
                .Select(c => new CatalogColumnAsset { ColumnName = c, DataType = "int", IsPrimaryKey = flagPrimaryKey }).ToList()
        };
        _client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<CatalogTableAsset> { asset });
        _repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(existing);
    }

    private static TableMetadata Stored(string[] pk, KeyColumnDirection[] dirs, string? owner = null, string sensitivity = "NORMAL",
        DataSourceType type = DataSourceType.Sql) => new()
    {
        Identifier = Orders,
        OwnerTenantId = owner,
        Table = new Table { SourceName = "ds", SourceType = "SqlServer", DataSourceType = type, Sensitivity = sensitivity },
        PrimaryKeyColumns = pk,
        PrimaryKeyDirections = dirs
    };

    [Fact]
    public async Task Sync_PhysicalDesc_PersistsDirectionsAndAudits()
    {
        // I403-T-30
        Catalog(["id", "ts"]);
        Physical(Known(Pk(["id", "ts"], A, D)));

        var result = await Sut().SyncCatalogAsync();

        result.Warnings.ShouldBeEmpty();
        _upserts.Single().PrimaryKeyDirections.ShouldBe([A, D]);
        _audits.Single().EventType.ShouldBe(AuditEventTypes.CatalogKeyDefinitionChanged);
        _audits.Single().ActorSid.Value.ShouldBe("system:catalog-sync");
        _audits.Single().DetailsJson.ShouldContain("\"directionSource\":\"physical\"");
        _audits.Single().DetailsJson.ShouldContain("\"DESC\"");
    }

    [Fact]
    public async Task Sync_PhysicalDirectionAudit_WithoutOwnerOrDefaultTenant_UsesThePlatformBucket()
    {
        // DT-A-6/DT-A-9: owner, else the sync tenant, else the default tenant, else the platform bucket.
        Catalog(["id", "ts"]);
        Physical(Known(Pk(["id", "ts"], A, D)));

        await Sut().SyncCatalogAsync();

        _audits.Single().TenantId.ShouldBe(TenantId.Platform);
    }

    [Fact]
    public async Task Sync_PhysicalDirectionAudit_WithOwner_UsesTheOwnerTenant()
    {
        Catalog(["id", "ts"], existing: Stored(["id", "ts"], [A, A], owner: "tenant-a"));
        Physical(Known(Pk(["id", "ts"], A, D)));

        await Sut().SyncCatalogAsync();

        _audits.Single().TenantId.ShouldBe(new TenantId("tenant-a"));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-07")]
    public async Task AuditThrows_NoPhysicalDirectionPersisted_AuditBeforeUpsertOrdering()
    {
        // I403-T-31 / I403-T-S07: audit strictly before upsert; a failing or cancelled audit keeps the pre-reconcile metadata
        foreach (Exception failure in new Exception[] { new InvalidOperationException("SENTINEL-AUDIT"), new OperationCanceledException() })
        {
            _calls.Clear();
            _upserts.Clear();
            Catalog(["id", "ts"]);
            Physical(Known(Pk(["id", "ts"], A, D)));
            _audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                _calls.Add("audit");
                return Task.FromException(failure);
            });

            var result = await Sut().SyncCatalogAsync();

            _calls.ShouldBe(["audit", "upsert", "epoch"]);
            _upserts.Single().PrimaryKeyDirections.ShouldBeEmpty();
            result.Warnings.ShouldBe(["Table 'sales.dbo.orders': KEY_DIRECTION_AUDIT_FAILED"]);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-08")]
    public async Task DryRun_NoAuditNoUpsertNoEpochInvalidation()
    {
        // I403-T-32 / I403-T-S08: the read and the findings run, nothing is written
        Catalog(["id", "ts"], Stored(["id", "ts"], [D, A]));
        Physical(Known(Pk(["id", "ts"], A, D)));

        var result = await Sut().SyncCatalogAsync(dryRun: true);

        result.Warnings.ShouldBe(["Table 'sales.dbo.orders': KEY_DIRECTION_CONFLICT"]);
        await _physical.Received(1).ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
        _calls.ShouldBeEmpty();
        await _audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-09")]
    public async Task Warnings_AuditAndLogs_NeverContainIndexColumnOrExceptionText()
    {
        // I403-T-33 / I403-T-S09: a code and the table id only
        Catalog(["secret_col_a", "secret_col_b"], Stored(["secret_col_a", "secret_col_b"], [D, A]));
        Physical(Known(new PhysicalKey(true, true, ["secret_col_a", "secret_col_b"], [A, D])));

        var result = await Sut().SyncCatalogAsync();

        result.Warnings.ShouldBe(["Table 'sales.dbo.orders': KEY_DIRECTION_CONFLICT"]);
        _audits.ShouldBeEmpty();   // stored kept, nothing physical applied, so nothing to audit

        Catalog(["secret_col_a", "secret_col_b"]);
        Physical(Known(new PhysicalKey(true, true, ["secret_col_a", "secret_col_b"], [A, D])));
        await Sut().SyncCatalogAsync();
        _audits.Single().DetailsJson.ShouldNotContain("secret_col");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-09")]
    public async Task Sync_RestrictToTenant_NoForeignFindings()
    {
        // I403-T-34: a table of another tenant is neither read nor reported
        Catalog(["id", "ts"], Stored(["id", "ts"], [D, A], owner: "tenant-b"));
        Physical(Known(Pk(["id", "ts"], A, D)));

        var result = await Sut().SyncCatalogAsync(restrictToTenant: new TenantId("tenant-a"));

        result.Warnings.ShouldBeEmpty();
        await _physical.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
        _upserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_SqliteAndDatabricks_DirectionUnknown_BehavesAsToday()
    {
        // I403-T-35: no key reader means Unsupported, the default snapshot
        foreach (var dialect in new[] { "SQLite", "Databricks" })
        {
            _upserts.Clear();
            _audits.Clear();
            Catalog(["id", "ts"], sourceType: dialect);

            var result = await Sut().SyncCatalogAsync();

            result.Warnings.ShouldBeEmpty();
            _upserts.Single().PrimaryKeyDirections.ShouldBeEmpty();
            _audits.ShouldBeEmpty();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-05")]
    public async Task AssetWithoutDirections_SameColumns_StoredDescRetained_FlagPath()
    {
        // I403-T-S04 (sync half): the asset flags the key columns (IsPrimaryKey path) without directions; the physical read is Unavailable
        foreach (var snapshot in new[] { PhysicalKeySnapshot.Unavailable, PhysicalKeySnapshot.Unsupported })
        {
            _upserts.Clear();
            Catalog(null, Stored(["id", "ts"], [A, D]), flagPrimaryKey: true);
            Physical(snapshot);

            await Sut().SyncCatalogAsync();

            _upserts.Single().PrimaryKeyDirections.ShouldBe([A, D]);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-05")]
    public async Task AssetWithoutDirections_ChangedColumns_DirectionsReset()
    {
        // I403-T-S05 (sync half)
        Catalog(["id"], Stored(["id", "ts"], [A, D]));
        Physical(PhysicalKeySnapshot.Unavailable);

        await Sut().SyncCatalogAsync();

        _upserts.Single().PrimaryKeyDirections.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-11")]
    public async Task AdmissionDeniedTable_NoPhysicalRead_NoFinding()
    {
        // I403-T-S11: a non-SQL table and an unsupported SQL dialect are never read
        Catalog(["id", "ts"], Stored(["id", "ts"], [D, A], type: DataSourceType.HttpDeclarative));
        var http = await Sut().SyncCatalogAsync();

        Catalog(["id", "ts"], sourceType: "Alation");
        var unsupported = await Sut().SyncCatalogAsync();

        await _physical.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
        http.Warnings.ShouldBeEmpty();
        unsupported.Warnings.ShouldNotContain(w => w.Contains("KEY_DIRECTION", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-12")]
    public async Task PhysicalDirections_NeverChangeSecurityFlags_RatchetStillApplied()
    {
        // I403-T-S12: a stored HIGH sensitivity is never lowered while physical directions are applied
        Catalog(["id", "ts"], Stored(["id"], [], sensitivity: "HIGH"));
        Physical(Known(Pk(["id", "ts"], A, D)));

        await Sut().SyncCatalogAsync();

        var upsert = _upserts.Single();
        upsert.PrimaryKeyDirections.ShouldBe([A, D]);
        upsert.Table.Sensitivity.ShouldBe("HIGH");
    }
}
