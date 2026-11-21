namespace Autheris.Extensions.Tests.Lakehouse;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class IcebergMetadataDiscoveryTests
{
    private static ClaimsPrincipal Analyst() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "analyst@corp.com"), new Claim(ClaimTypes.PrimarySid, "analyst@corp.com")],
        "Bearer"));

    private static string SampleMetadataJson(string tableLocation) => $$"""
        {
          "format-version": 2,
          "table-uuid": "uuid-1",
          "location": "{{tableLocation.Replace("\\", "\\\\")}}",
          "current-snapshot-id": 1,
          "current-schema-id": 0,
          "schemas": [
            {
              "schema-id": 0,
              "fields": [
                { "id": 1, "name": "id", "type": "string", "required": true },
                { "id": 2, "name": "secret_col", "type": "string", "required": false }
              ]
            }
          ],
          "partition-specs": [ { "spec-id": 0, "fields": [] } ],
          "snapshots": []
        }
        """;

    [Fact]
    public async Task LoadTableAsync_DirectoryWithVersionHint_DiscoversHintedVersionNotHardcodedV2()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "iceberg_disc_" + Guid.NewGuid().ToString("N"));
        try
        {
            var metaDir = Path.Combine(tempRoot, "orders", "metadata");
            Directory.CreateDirectory(metaDir);
            await File.WriteAllTextAsync(Path.Combine(metaDir, "version-hint.text"), "5\n");
            await File.WriteAllTextAsync(Path.Combine(metaDir, "v5.metadata.json"), SampleMetadataJson("orders"));

            var options = Options.Create(new GatewayOptions
            {
                Lakehouse = new LakehouseOptions { Storage = new LakehouseStorageOptions { LocalBasePath = tempRoot } }
            });
            var storage = new LocalStorageProvider(options);
            var reader = new IcebergMetadataReader(storage, NullLogger<IcebergMetadataReader>.Instance);

            var meta = new TableMetadata
            {
                Identifier = new TableIdentifier("tenant-1", "raw", "orders"),
                Table = new Table
                {
                    SourceName = "tenant-1",
                    SchemaName = "raw",
                    TableName = "orders",
                    DataSourceType = DataSourceType.LakehouseIceberg,
                    Location = "orders",
                    IsActive = true
                },
                Columns = [new TableColumn { ColumnName = "id", DataType = "string" }]
            };

            var audit = new RecordingAuditRepo();
            var svc = new IcebergRestCatalogFederationService(
                reader,
                new StaticTableRepo(meta),
                options,
                NullLogger<IcebergRestCatalogFederationService>.Instance,
                audit,
                new AllowingPdp(),
                transportGate: new AllowingTransportGate());

            var response = await svc.LoadTableAsync("tenant-1", "raw", "orders", Analyst());

            response.MetadataLocation.ShouldEndWith("v5.metadata.json");
            response.Metadata.ShouldNotBeNull();
            response.Metadata!.CurrentSchema.Fields.Count.ShouldBe(1);
            response.Metadata.CurrentSchema.Fields[0].Name.ShouldBe("id");
            audit.Entries.Count.ShouldBe(1);
            audit.Entries[0].Decision.ShouldBe("ALLOW");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task LoadTableAsync_DirectoryWithoutVersionHint_DiscoversHighestUuidOrVersionMetadataFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "iceberg_disc_uuid_" + Guid.NewGuid().ToString("N"));
        try
        {
            var metaDir = Path.Combine(tempRoot, "orders", "metadata");
            Directory.CreateDirectory(metaDir);
            await File.WriteAllTextAsync(Path.Combine(metaDir, "00002-11111111-1111-1111-1111-111111111111.metadata.json"), SampleMetadataJson("orders"));
            await File.WriteAllTextAsync(Path.Combine(metaDir, "00010-22222222-2222-2222-2222-222222222222.metadata.json"), SampleMetadataJson("orders"));

            var options = Options.Create(new GatewayOptions
            {
                Lakehouse = new LakehouseOptions { Storage = new LakehouseStorageOptions { LocalBasePath = tempRoot } }
            });
            var storage = new LocalStorageProvider(options);
            var reader = new IcebergMetadataReader(storage, NullLogger<IcebergMetadataReader>.Instance);

            var meta = new TableMetadata
            {
                Identifier = new TableIdentifier("tenant-1", "raw", "orders"),
                Table = new Table
                {
                    SourceName = "tenant-1",
                    SchemaName = "raw",
                    TableName = "orders",
                    DataSourceType = DataSourceType.LakehouseIceberg,
                    Location = "orders",
                    IsActive = true
                },
                Columns = [new TableColumn { ColumnName = "id", DataType = "string" }]
            };

            var svc = new IcebergRestCatalogFederationService(
                reader,
                new StaticTableRepo(meta),
                options,
                NullLogger<IcebergRestCatalogFederationService>.Instance,
                new RecordingAuditRepo(),
                new AllowingPdp(),
                transportGate: new AllowingTransportGate());

            var response = await svc.LoadTableAsync("tenant-1", "raw", "orders", Analyst());

            response.MetadataLocation.ShouldEndWith("00010-22222222-2222-2222-2222-222222222222.metadata.json");
            response.Metadata.ShouldNotBeNull();
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task LoadTableAsync_WhenMetadataFileMissing_FailsClosedAndDoesNotAuditAllow()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "iceberg_missing_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var options = Options.Create(new GatewayOptions
            {
                Lakehouse = new LakehouseOptions { Storage = new LakehouseStorageOptions { LocalBasePath = tempRoot } }
            });
            var storage = new LocalStorageProvider(options);
            var reader = new IcebergMetadataReader(storage, NullLogger<IcebergMetadataReader>.Instance);

            var meta = new TableMetadata
            {
                Identifier = new TableIdentifier("tenant-1", "raw", "orders"),
                Table = new Table
                {
                    SourceName = "tenant-1",
                    SchemaName = "raw",
                    TableName = "orders",
                    DataSourceType = DataSourceType.LakehouseIceberg,
                    Location = "orders/metadata/v99.metadata.json",
                    IsActive = true
                },
                Columns = [new TableColumn { ColumnName = "id", DataType = "string" }]
            };

            var audit = new RecordingAuditRepo();
            var svc = new IcebergRestCatalogFederationService(
                reader,
                new StaticTableRepo(meta),
                options,
                NullLogger<IcebergRestCatalogFederationService>.Instance,
                audit,
                new AllowingPdp(),
                transportGate: new AllowingTransportGate());

            await Should.ThrowAsync<FileNotFoundException>(() =>
                svc.LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());

            audit.Entries.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task LoadTableAsync_WhenMetadataJsonCorrupt_FailsClosedAndDoesNotAuditAllow()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "iceberg_corrupt_" + Guid.NewGuid().ToString("N"));
        try
        {
            var metaDir = Path.Combine(tempRoot, "orders", "metadata");
            Directory.CreateDirectory(metaDir);
            await File.WriteAllTextAsync(Path.Combine(metaDir, "v2.metadata.json"), "{ not-valid-json");

            var options = Options.Create(new GatewayOptions
            {
                Lakehouse = new LakehouseOptions { Storage = new LakehouseStorageOptions { LocalBasePath = tempRoot } }
            });
            var storage = new LocalStorageProvider(options);
            var reader = new IcebergMetadataReader(storage, NullLogger<IcebergMetadataReader>.Instance);

            var meta = new TableMetadata
            {
                Identifier = new TableIdentifier("tenant-1", "raw", "orders"),
                Table = new Table
                {
                    SourceName = "tenant-1",
                    SchemaName = "raw",
                    TableName = "orders",
                    DataSourceType = DataSourceType.LakehouseIceberg,
                    Location = "orders/metadata/v2.metadata.json",
                    IsActive = true
                },
                Columns = [new TableColumn { ColumnName = "id", DataType = "string" }]
            };

            var audit = new RecordingAuditRepo();
            var svc = new IcebergRestCatalogFederationService(
                reader,
                new StaticTableRepo(meta),
                options,
                NullLogger<IcebergRestCatalogFederationService>.Instance,
                audit,
                new AllowingPdp(),
                transportGate: new AllowingTransportGate());

            await Should.ThrowAsync<JsonException>(() =>
                svc.LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());

            audit.Entries.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task LoadTableAsync_WhenVersionHintContainsTraversal_FailsClosedWithSecurityException()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "iceberg_trav_" + Guid.NewGuid().ToString("N"));
        try
        {
            var metaDir = Path.Combine(tempRoot, "orders", "metadata");
            Directory.CreateDirectory(metaDir);
            await File.WriteAllTextAsync(Path.Combine(metaDir, "version-hint.text"), "../../etc/passwd");

            var options = Options.Create(new GatewayOptions
            {
                Lakehouse = new LakehouseOptions { Storage = new LakehouseStorageOptions { LocalBasePath = tempRoot } }
            });
            var storage = new LocalStorageProvider(options);
            var reader = new IcebergMetadataReader(storage, NullLogger<IcebergMetadataReader>.Instance);

            var meta = new TableMetadata
            {
                Identifier = new TableIdentifier("tenant-1", "raw", "orders"),
                Table = new Table
                {
                    SourceName = "tenant-1",
                    SchemaName = "raw",
                    TableName = "orders",
                    DataSourceType = DataSourceType.LakehouseIceberg,
                    Location = "orders",
                    IsActive = true
                },
                Columns = [new TableColumn { ColumnName = "id", DataType = "string" }]
            };

            var svc = new IcebergRestCatalogFederationService(
                reader,
                new StaticTableRepo(meta),
                options,
                NullLogger<IcebergRestCatalogFederationService>.Instance,
                new RecordingAuditRepo(),
                new AllowingPdp(),
                transportGate: new AllowingTransportGate());

            await Should.ThrowAsync<SecurityException>(() =>
                svc.LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task HandleIcebergError_FileNotFound_Returns404ErrorModelWithoutLeakingMessage()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var ctx = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };
        const string secretPath = "/internal/secret/bucket/v99.metadata.json";

        var result = IcebergRestCatalogEndpoints.HandleIcebergError(ctx, new FileNotFoundException(secretPath));
        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        var body = await reader.ReadToEndAsync();
        body.ShouldContain("NO_SUCH_TABLE");
        body.ShouldNotContain(secretPath);
    }

    private sealed class AllowingPdp : IAccessDecisionPoint
    {
        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(TableAccessDecision.Allowed(
                request.Object.Identifier,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["id"] = ColumnAccessLevel.Clear },
                hasUnconstrainedColumnAllow: true));

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
            SecurityPrincipalContext subject,
            IReadOnlyList<TableMetadata> objects,
            AccessOperation operation,
            AccessTransport transport,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class AllowingTransportGate : IDataSourceTransportGate
    {
        public DataSourceTransportFlags Ingress => DataSourceTransportFlags.Rest;
        public IReadOnlyList<string> CheckedSources => [];
        public Task EnsureAsync(string? dataSourceName, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureAllAsync(IEnumerable<string?> dataSourceNames, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureCatalogSourcesAsync(IEnumerable<string?> catalogSourceNames, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureCatalogSourceAsync(string? catalogSourceName, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsAllowed(string? dataSourceName) => true;
    }

    private sealed class RecordingAuditRepo : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
            string? targetTable = null,
            Sid? actorSid = null,
            DateTimeOffset? since = null,
            int limit = 1000,
            TenantId? tenantId = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class StaticTableRepo(TableMetadata meta) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(meta);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>([meta]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
            Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }
}
