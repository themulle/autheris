using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Unit.DataCatalog;

public sealed class PhysicalKeyRefreshServiceTests
{
    private const KeyColumnDirection A = KeyColumnDirection.Ascending;
    private const KeyColumnDirection D = KeyColumnDirection.Descending;

    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IPhysicalKeyCatalog _physicalKeys = Substitute.For<IPhysicalKeyCatalog>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly IEpochValidationService _epochService = Substitute.For<IEpochValidationService>();
    private readonly Sid _actor = new("user:alice");

    private static TableMetadata CreateSqlTable(string domain, string schema, string table, string[] pk, KeyColumnDirection[]? pkDirs = null, string[]? columns = null) => new()
    {
        Identifier = new TableIdentifier(domain, schema, table),
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = domain,
            SchemaName = schema,
            TableName = table,
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns = (columns ?? pk).Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList(),
        PrimaryKeyColumns = pk,
        PrimaryKeyDirections = pkDirs ?? [],
        Indexes = []
    };

    [Fact]
    [Trait("Category", "Security")]
    public async Task CAT427_T06_PhysicalKeyRefreshService_DryRun_DoesNotMutateStoreOrAudit()
    {
        var orders = CreateSqlTable("default", "dbo", "orders", ["client_id", "ts"]);
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([orders]);

        var physicalKey = new PhysicalKey(true, true, ["client_id", "ts"], [A, D]);
        var snapshot = new PhysicalKeySnapshot(PhysicalKeySnapshotState.Known, [physicalKey], TargetSqlDialect.SqlServer);
        _physicalKeys.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(snapshot));

        var service = new PhysicalKeyRefreshService(
            _metadataRepo,
            _physicalKeys,
            _auditWriter,
            _epochService,
            null,
            NullLogger<PhysicalKeyRefreshService>.Instance);

        var request = new PhysicalKeyRefreshRequest(DryRun: true);
        var result = await service.RefreshDatasourceKeysAsync("default", request, _actor, CancellationToken.None);

        result.DryRun.ShouldBeTrue();
        result.TablesScanned.ShouldBe(1);
        result.KeysUpdated.ShouldBe(1);
        result.Details.Count.ShouldBe(1);
        result.Details[0].Status.ShouldBe("Updated");

        // Fail-Closed / Zero-Trust invariant: dryRun MUST NOT write to repo, epoch, or audit
        await _metadataRepo.DidNotReceiveWithAnyArgs().ReplaceTableKeyDefinitionAsync(default, default!, default!);
        await _epochService.DidNotReceiveWithAnyArgs().InvalidateEpochAsync(default);
        await _auditWriter.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CAT427_T07_PhysicalKeyRefreshService_WhenPhysicalApplied_AuditsAndPersists()
    {
        var orders = CreateSqlTable("default", "dbo", "orders", ["client_id", "ts"]);
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([orders]);

        var physicalKey = new PhysicalKey(true, true, ["client_id", "ts"], [A, D]);
        var snapshot = new PhysicalKeySnapshot(PhysicalKeySnapshotState.Known, [physicalKey], TargetSqlDialect.SqlServer);
        _physicalKeys.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(snapshot));

        var service = new PhysicalKeyRefreshService(
            _metadataRepo,
            _physicalKeys,
            _auditWriter,
            _epochService,
            null,
            NullLogger<PhysicalKeyRefreshService>.Instance);

        var request = new PhysicalKeyRefreshRequest(DryRun: false);
        var result = await service.RefreshDatasourceKeysAsync("default", request, _actor, CancellationToken.None);

        result.DryRun.ShouldBeFalse();
        result.KeysUpdated.ShouldBe(1);

        // Audit strictly recorded before persist
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == AuditEventTypes.CatalogKeyDefinitionChanged &&
                e.ActorSid == _actor &&
                e.TargetTable == orders.Identifier.ToString()),
            Arg.Any<CancellationToken>());

        await _metadataRepo.Received(1).ReplaceTableKeyDefinitionAsync(
            orders.Identifier,
            Arg.Is<IReadOnlyList<string>>(pk => pk.SequenceEqual(orders.PrimaryKeyColumns)),
            Arg.Is<IReadOnlyList<TableIndexDefinition>>(idx => idx.Count == 0),
            Arg.Is<IReadOnlyList<KeyColumnDirection>>(d => d.SequenceEqual(new[] { A, D })),
            Arg.Any<CancellationToken>());

        await _epochService.Received(1).InvalidateEpochAsync(orders.Identifier, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PhysicalKeyRefreshService_TableFilter_OnlyRefreshesMatchingTable()
    {
        var orders = CreateSqlTable("default", "dbo", "orders", ["id"]);
        var customers = CreateSqlTable("default", "dbo", "customers", ["id"]);
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([orders, customers]);

        var physicalKey = new PhysicalKey(true, true, ["id"], [A]);
        var snapshot = new PhysicalKeySnapshot(PhysicalKeySnapshotState.Known, [physicalKey], TargetSqlDialect.SqlServer);
        _physicalKeys.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(snapshot));

        var service = new PhysicalKeyRefreshService(
            _metadataRepo,
            _physicalKeys,
            _auditWriter,
            _epochService,
            null,
            NullLogger<PhysicalKeyRefreshService>.Instance);

        var request = new PhysicalKeyRefreshRequest(DryRun: true, TableFilter: "orders");
        var result = await service.RefreshDatasourceKeysAsync("default", request, _actor, CancellationToken.None);

        result.TablesScanned.ShouldBe(1);
        result.Details.Count.ShouldBe(1);
        result.Details[0].Table.ShouldContain("orders");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PhysicalKeyRefreshService_WhenTenantRestricted_OnlyRefreshesTenantTables()
    {
        var tenantOrders = CreateSqlTable("default", "dbo", "orders", ["id"]) with { OwnerTenantId = "tenant-a" };
        var otherOrders = CreateSqlTable("default", "dbo", "other", ["id"]) with { OwnerTenantId = "tenant-b" };
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([tenantOrders, otherOrders]);

        var physicalKey = new PhysicalKey(true, true, ["id"], [A]);
        var snapshot = new PhysicalKeySnapshot(PhysicalKeySnapshotState.Known, [physicalKey], TargetSqlDialect.SqlServer);
        _physicalKeys.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(snapshot));

        var service = new PhysicalKeyRefreshService(
            _metadataRepo,
            _physicalKeys,
            _auditWriter,
            _epochService,
            null,
            NullLogger<PhysicalKeyRefreshService>.Instance);

        var request = new PhysicalKeyRefreshRequest(DryRun: true);
        var result = await service.RefreshDatasourceKeysAsync("default", request, _actor, new TenantId("tenant-a"), CancellationToken.None);

        result.TablesScanned.ShouldBe(1);
        result.Details.Count.ShouldBe(1);
        result.Details[0].Table.ShouldContain("orders");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PhysicalKeyRefreshService_WhenNonUniqueIndexesChanged_AuditsAndPersistsAndInvalidatesEpoch()
    {
        var orders = CreateSqlTable("default", "dbo", "orders", ["id"], [A], columns: ["id", "created_at"]);
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([orders]);

        var physicalIndexRepo = Substitute.For<IPhysicalIndexRepository>();
        var nonUniqueIdx = new PhysicalNonUniqueIndex(["created_at"], [D]);
        var snapshot = new PhysicalKeySnapshot(
            PhysicalKeySnapshotState.Known,
            [new PhysicalKey(true, true, ["id"], [A])],
            TargetSqlDialect.SqlServer,
            new PhysicalNonUniqueSnapshot(PhysicalKeySnapshotState.Known, [nonUniqueIdx]));

        _physicalKeys.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(snapshot));

        var service = new PhysicalKeyRefreshService(
            _metadataRepo,
            _physicalKeys,
            _auditWriter,
            _epochService,
            physicalIndexRepo,
            NullLogger<PhysicalKeyRefreshService>.Instance);

        var request = new PhysicalKeyRefreshRequest(DryRun: false);
        var result = await service.RefreshDatasourceKeysAsync("default", request, _actor, CancellationToken.None);

        result.IndexesUpdated.ShouldBe(1);

        // Required audit before persistence
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == AuditEventTypes.CatalogPhysicalIndexesChanged &&
                e.ActorSid == _actor &&
                e.TargetTable == orders.Identifier.ToString()),
            Arg.Any<CancellationToken>());

        await physicalIndexRepo.Received(1).ReplacePhysicalIndexesAsync(
            orders.Identifier,
            Arg.Any<IReadOnlyList<PhysicalIndexDefinition>>(),
            Arg.Any<CancellationToken>());

        await _epochService.Received(1).InvalidateEpochAsync(orders.Identifier, Arg.Any<CancellationToken>());
    }
}
