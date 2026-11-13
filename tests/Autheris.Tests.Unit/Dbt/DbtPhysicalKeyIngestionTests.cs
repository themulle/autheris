using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Unit.Dbt;

public sealed class DbtPhysicalKeyIngestionTests
{
    private const KeyColumnDirection A = KeyColumnDirection.Ascending;
    private const KeyColumnDirection D = KeyColumnDirection.Descending;

    private static readonly DbtGovernanceImportCaller DefaultCaller =
        new(new VirtualFilterActor(new Sid("user:alice"), false), new TenantId("default"), IsClusterAdmin: true, IsGovernanceAdmin: true);

    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IDbtProposalRepository _proposalRepo = Substitute.For<IDbtProposalRepository>();
    private readonly ILineageGraphStore _lineageStore = Substitute.For<ILineageGraphStore>();
    private readonly IPhysicalKeyCatalog _physicalKeys = Substitute.For<IPhysicalKeyCatalog>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly IOptions<GatewayOptions> _gatewayOptions = Microsoft.Extensions.Options.Options.Create(new GatewayOptions());

    private static TableMetadata CreateStoredSqlTable(string domain, string schema, string table) => new()
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
        Columns =
        [
            new TableColumn { ColumnName = "client_id", DataType = "varchar" },
            new TableColumn { ColumnName = "ts", DataType = "datetime2" }
        ],
        PrimaryKeyColumns = [],
        PrimaryKeyDirections = [],
        Indexes = []
    };

    private static string CreateManifestJson(string modelName, string[] pkColumns)
    {
        var columns = new Dictionary<string, object>
        {
            ["client_id"] = new { name = "client_id", data_type = "varchar", description = "Client ID" },
            ["ts"] = new { name = "ts", data_type = "datetime2", description = "Timestamp" }
        };

        var constraints = new List<object>
        {
            new
            {
                type = "primary_key",
                columns = pkColumns
            }
        };

        var nodes = new Dictionary<string, object>
        {
            [$"model.test.{modelName}"] = new
            {
                name = modelName,
                database = "default",
                schema = "dbo",
                description = "Test model",
                config = new { materialized = "table" },
                tags = new[] { "prod" },
                meta = new Dictionary<string, object>(),
                columns,
                constraints,
                depends_on = new { nodes = Array.Empty<string>() }
            }
        };

        return JsonSerializer.Serialize(new
        {
            metadata = new { dbt_version = "1.8.0" },
            nodes,
            sources = new Dictionary<string, object>()
        });
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CAT427_T01_DbtIngestion_WhenSqlTableHasClusteredDescKey_ReconcilesPhysicalDirections()
    {
        var existing = CreateStoredSqlTable("default", "dbo", "orders");
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        TableMetadata? persisted = null;
        await _metadataRepo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => persisted = m), Arg.Any<CancellationToken>());

        var physicalKey = new PhysicalKey(true, true, ["client_id", "ts"], [A, D]);
        var snapshot = new PhysicalKeySnapshot(PhysicalKeySnapshotState.Known, [physicalKey], TargetSqlDialect.SqlServer);
        _physicalKeys.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(snapshot));

        var service = new DbtMetadataIngestionService(
            _proposalRepo,
            _metadataRepo,
            _lineageStore,
            null,
            _gatewayOptions,
            null,
            null,
            null,
            null,
            null,
            null,
            _physicalKeys,
            _auditWriter,
            NullLogger<DbtMetadataIngestionService>.Instance);

        var manifestJson = CreateManifestJson("orders", ["client_id", "ts"]);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifestJson));

        var result = await service.IngestManifestStreamAsync(stream, DefaultCaller, dryRun: false, CancellationToken.None);

        result.Success.ShouldBeTrue();
        persisted.ShouldNotBeNull();
        persisted.PrimaryKeyColumns.ShouldBe(["client_id", "ts"]);
        persisted.PrimaryKeyDirections.ShouldBe([A, D]);

        // Audit entry strictly verified
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == AuditEventTypes.CatalogKeyDefinitionChanged &&
                e.ActorSid == new Sid("system:dbt-sync") &&
                e.TargetTable == existing.Identifier.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CAT427_T02_DbtIngestion_WhenExplicitDirectionDeclared_DeclaredPrecedenceWins()
    {
        var existing = CreateStoredSqlTable("default", "dbo", "orders");
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        TableMetadata? persisted = null;
        await _metadataRepo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => persisted = m), Arg.Any<CancellationToken>());

        // Physical database has [A, D]
        var physicalKey = new PhysicalKey(true, true, ["client_id", "ts"], [A, D]);
        var snapshot = new PhysicalKeySnapshot(PhysicalKeySnapshotState.Known, [physicalKey], TargetSqlDialect.SqlServer);
        _physicalKeys.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(snapshot));

        var service = new DbtMetadataIngestionService(
            _proposalRepo,
            _metadataRepo,
            _lineageStore,
            null,
            _gatewayOptions,
            null,
            null,
            null,
            null,
            null,
            null,
            _physicalKeys,
            _auditWriter,
            NullLogger<DbtMetadataIngestionService>.Instance);

        // Manifest explicitly declares DESC on client_id: ["client_id DESC", "ts ASC"]
        var manifestJson = CreateManifestJson("orders", ["client_id DESC", "ts ASC"]);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifestJson));

        var result = await service.IngestManifestStreamAsync(stream, DefaultCaller, dryRun: false, CancellationToken.None);

        result.Success.ShouldBeTrue();
        persisted.ShouldNotBeNull();
        // Declared wins over physical
        persisted.PrimaryKeyDirections.ShouldBe([D, A]);
        result.Warnings.ShouldContain(w => w.Contains(PhysicalKeyDirectionReconciler.DriftCode));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CAT427_T03_DbtIngestion_WhenPhysicalKeyColumnsMismatch_ReconcilesNoDirections()
    {
        var existing = CreateStoredSqlTable("default", "dbo", "orders");
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        TableMetadata? persisted = null;
        await _metadataRepo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => persisted = m), Arg.Any<CancellationToken>());

        // Physical database index is composite ["client_id", "ts"], but manifest declares only ["client_id"]
        var physicalKey = new PhysicalKey(true, true, ["client_id", "ts"], [A, D]);
        var snapshot = new PhysicalKeySnapshot(PhysicalKeySnapshotState.Known, [physicalKey], TargetSqlDialect.SqlServer);
        _physicalKeys.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(snapshot));

        var service = new DbtMetadataIngestionService(
            _proposalRepo,
            _metadataRepo,
            _lineageStore,
            null,
            _gatewayOptions,
            null,
            null,
            null,
            null,
            null,
            null,
            _physicalKeys,
            _auditWriter,
            NullLogger<DbtMetadataIngestionService>.Instance);

        var manifestJson = CreateManifestJson("orders", ["client_id"]);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifestJson));

        var result = await service.IngestManifestStreamAsync(stream, DefaultCaller, dryRun: false, CancellationToken.None);

        result.Success.ShouldBeTrue();
        persisted.ShouldNotBeNull();
        persisted.PrimaryKeyColumns.ShouldBe(["client_id"]);
        // Mismatch fails closed: no direction inferred
        persisted.PrimaryKeyDirections.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CAT427_T04_DbtIngestion_WhenPhysicalReadFails_RetainsDeclaredWithoutError()
    {
        var existing = CreateStoredSqlTable("default", "dbo", "orders");
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        TableMetadata? persisted = null;
        await _metadataRepo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => persisted = m), Arg.Any<CancellationToken>());

        _physicalKeys.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(PhysicalKeySnapshot.Unavailable));

        var service = new DbtMetadataIngestionService(
            _proposalRepo,
            _metadataRepo,
            _lineageStore,
            null,
            _gatewayOptions,
            null,
            null,
            null,
            null,
            null,
            null,
            _physicalKeys,
            _auditWriter,
            NullLogger<DbtMetadataIngestionService>.Instance);

        var manifestJson = CreateManifestJson("orders", ["client_id", "ts"]);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifestJson));

        var result = await service.IngestManifestStreamAsync(stream, DefaultCaller, dryRun: false, CancellationToken.None);

        result.Success.ShouldBeTrue();
        persisted.ShouldNotBeNull();
        persisted.PrimaryKeyDirections.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CAT427_T05_DbtIngestion_WhenAuditFails_RollsBackToWithoutPhysical()
    {
        var existing = CreateStoredSqlTable("default", "dbo", "orders");
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        TableMetadata? persisted = null;
        await _metadataRepo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => persisted = m), Arg.Any<CancellationToken>());

        var physicalKey = new PhysicalKey(true, true, ["client_id", "ts"], [A, D]);
        var snapshot = new PhysicalKeySnapshot(PhysicalKeySnapshotState.Known, [physicalKey], TargetSqlDialect.SqlServer);
        _physicalKeys.ReadAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(snapshot));

        // Audit writer throws
        _auditWriter.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Audit storage unreachable"));

        var service = new DbtMetadataIngestionService(
            _proposalRepo,
            _metadataRepo,
            _lineageStore,
            null,
            _gatewayOptions,
            null,
            null,
            null,
            null,
            null,
            null,
            _physicalKeys,
            _auditWriter,
            NullLogger<DbtMetadataIngestionService>.Instance);

        var manifestJson = CreateManifestJson("orders", ["client_id", "ts"]);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifestJson));

        var result = await service.IngestManifestStreamAsync(stream, DefaultCaller, dryRun: false, CancellationToken.None);

        result.Success.ShouldBeTrue();
        persisted.ShouldNotBeNull();
        // Rollback to without physical because audit failed
        persisted.PrimaryKeyDirections.ShouldBeEmpty();
    }
}
