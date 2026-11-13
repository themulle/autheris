namespace Autheris.Modules.Lakehouse.Tests;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.DataSources.Relations;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class LakehouseGovernanceTests
{
    private static readonly TenantId DefaultTenant = new("tenant-1");

    private static ClaimsPrincipal CreatePrincipal(string sid = "S-1-5-21-LAKEHOUSE-GOV", string tenant = "tenant-1") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, sid),
            new Claim(ClaimTypes.Name, "analyst@corp.com"),
            new Claim("tenant_id", tenant)
        ], "Bearer"));

    [Fact]
    public async Task Delta_query_applies_row_filter_and_masking()
    {
        var tableId = new TableIdentifier("lake", "sales", "delta_orders");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "lake", SchemaName = "sales", TableName = "delta_orders", DataSourceType = DataSourceType.LakehouseDelta, IsActive = true },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "string" },
                new TableColumn { ColumnName = "region", DataType = "string" },
                new TableColumn { ColumnName = "email", DataType = "string", IsSensitive = true },
                new TableColumn { ColumnName = "tenantId", DataType = "string" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new MaskingRule { RuleType = "REDACT" }
            }
        };

        var snapshot = CreateDeltaSnapshot(
            "delta-1",
            ["id", "region", "email", "tenantId"],
            ["tenantId", "region"],
            ("part-eu.parquet", new Dictionary<string, string> { ["tenantId"] = "tenant-1", ["region"] = "EU" }),
            ("part-us.parquet", new Dictionary<string, string> { ["tenantId"] = "tenant-1", ["region"] = "US" }));

        var deltaExecutor = new DeltaLakeDataSourceExecutor(
            new StubDeltaMetadataReader(snapshot),
            new DeltaPartitionPruner(NullLogger<DeltaPartitionPruner>.Instance),
            new RedactingMaskingProvider(),
            Options.Create(new GatewayOptions()),
            NullLogger<DeltaLakeDataSourceExecutor>.Instance,
            new DemoDataSwitch(true));

        var decision = TableAccessDecision.Allowed(
            tableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["region"] = ColumnAccessLevel.Clear,
                ["email"] = ColumnAccessLevel.Mask,
                ["tenantId"] = ColumnAccessLevel.Clear
            },
            rowFilterSql: "region = 'EU'");

        var service = BuildGatewayExecutionService(
            new InMemoryTableMetadataRepository(metadata),
            new Dictionary<TableIdentifier, TableAccessDecision> { [tableId] = decision },
            deltaExecutor,
            new RecordingAuditLogRepository());

        var (rows, _) = await service.ExecuteTableQueryAsync(
            CreatePrincipal(),
            tableId,
            first: 10,
            after: 0,
            queryArguments: null,
            requestedFields: ["id", "region", "email", "tenantId"]);

        rows.Count.ShouldBe(1);
        rows[0]["region"].ShouldBe("EU");
        rows[0]["email"].ShouldBe("[REDACTED]");
        rows[0]["tenantId"].ShouldBe("tenant-1");
    }

    [Fact]
    public async Task Delta_rejects_undeclared_columns()
    {
        var tableId = new TableIdentifier("lake", "sales", "delta_orders");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "lake", SchemaName = "sales", TableName = "delta_orders", DataSourceType = DataSourceType.LakehouseDelta, IsActive = true },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "string" },
                new TableColumn { ColumnName = "tenantId", DataType = "string" }
            ]
        };

        var snapshotWithUndeclaredColumn = CreateDeltaSnapshot(
            "delta-undeclared",
            ["id", "tenantId", "internal_salary"],
            ["tenantId"],
            ("part-0.parquet", new Dictionary<string, string> { ["tenantId"] = "tenant-1" }));

        var executor = new DeltaLakeDataSourceExecutor(
            new StubDeltaMetadataReader(snapshotWithUndeclaredColumn),
            new DeltaPartitionPruner(NullLogger<DeltaPartitionPruner>.Instance),
            new RedactingMaskingProvider(),
            Options.Create(new GatewayOptions()),
            NullLogger<DeltaLakeDataSourceExecutor>.Instance,
            new DemoDataSwitch(true));

        var decision = TableAccessDecision.Allowed(
            tableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["tenantId"] = ColumnAccessLevel.Clear
            });

        // 1. Selecting an undeclared column present in the Delta log is rejected
        var selectUndeclaredCtx = new DataSourceExecutionContext(
            "lake", metadata, CreatePrincipal(), decision, new Dictionary<string, object?>(), ["id", "internal_salary"], null, 10, 0, DefaultTenant);
        await Should.ThrowAsync<GatewaySecurityException>(() => executor.ExecuteAsync(selectUndeclaredCtx));

        // 2. Filtering on an undeclared column present in the Delta log is rejected
        var filterUndeclaredCtx = new DataSourceExecutionContext(
            "lake", metadata, CreatePrincipal(), decision, new Dictionary<string, object?> { ["internal_salary"] = "100000" }, ["id"], null, 10, 0, DefaultTenant);
        await Should.ThrowAsync<GatewaySecurityException>(() => executor.ExecuteAsync(filterUndeclaredCtx));

        // 3. Unspecified projection (RequestedFields empty) serves only declared catalog columns and never leaks internal_salary
        var wildcardCtx = new DataSourceExecutionContext(
            "lake", metadata, CreatePrincipal(), decision, new Dictionary<string, object?>(), Array.Empty<string>(), null, 10, 0, DefaultTenant);
        var rows = await executor.ExecuteAsync(wildcardCtx);
        rows.Count.ShouldBe(1);
        rows[0].ContainsKey("id").ShouldBeTrue();
        rows[0].ContainsKey("tenantId").ShouldBeTrue();
        rows[0].ContainsKey("internal_salary").ShouldBeFalse();
    }

    [Fact]
    public async Task Iceberg_load_omits_undeclared_columns()
    {
        const string metadataJson = """
        {
          "format-version": 2,
          "table-uuid": "iceberg-uuid-01",
          "location": "s3://lake/raw/orders",
          "current-snapshot-id": 1001,
          "current-schema-id": 0,
          "schemas": [
            {
              "schema-id": 0,
              "fields": [
                { "id": 1, "name": "id", "type": "string", "required": true },
                { "id": 2, "name": "name", "type": "string", "required": false },
                { "id": 3, "name": "secret_token", "type": "string", "required": false }
              ]
            }
          ],
          "partition-specs": [ { "spec-id": 0, "fields": [] } ],
          "snapshots": [
            { "snapshot-id": 1001, "timestamp-ms": 1700000000000, "manifest-list": "s3://lake/raw/orders/metadata/manifest.avro" }
          ]
        }
        """;

        var tableId = new TableIdentifier("tenant-1", "raw", "orders");
        var catalogMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceName = "tenant-1",
                SchemaName = "raw",
                TableName = "orders",
                DataSourceType = DataSourceType.LakehouseIceberg,
                Location = "s3://lake/raw/orders/metadata/v2.metadata.json",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "string" },
                new TableColumn { ColumnName = "name", DataType = "string" }
            ]
        };

        var adp = new RecordingAccessDecisionPoint(req =>
            TableAccessDecision.Allowed(
                req.Object.Identifier,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["name"] = ColumnAccessLevel.Clear
                },
                hasUnconstrainedColumnAllow: true));

        var service = new IcebergRestCatalogFederationService(
            new IcebergMetadataReader(new StubLakehouseStorageProvider(metadataJson), NullLogger<IcebergMetadataReader>.Instance),
            new InMemoryTableMetadataRepository(catalogMeta),
            Options.Create(new GatewayOptions { Lakehouse = new LakehouseOptions { Enabled = true } }),
            NullLogger<IcebergRestCatalogFederationService>.Instance,
            new RecordingAuditLogRepository(),
            accessDecisionPoint: adp,
            transportGate: NSubstitute.Substitute.For<Autheris.Application.DataSources.IDataSourceTransportGate>());

        var response = await service.LoadTableAsync("tenant-1", "raw", "orders", CreatePrincipal());

        response.Metadata.ShouldNotBeNull();
        response.Metadata!.CurrentSchema.Fields.Select(f => f.Name).ShouldBe(["id", "name"]);
        response.Metadata.CurrentSchema.Fields.Any(f => f.Name == "secret_token").ShouldBeFalse();
    }

    [Fact]
    public async Task Iceberg_load_and_credential_vending_decide_via_access_decision_point()
    {
        const string metadataJson = """
        {
          "format-version": 2,
          "table-uuid": "iceberg-uuid-02",
          "location": "s3://lake/raw/orders",
          "current-snapshot-id": 1001,
          "current-schema-id": 0,
          "schemas": [
            {
              "schema-id": 0,
              "fields": [
                { "id": 1, "name": "id", "type": "string", "required": true }
              ]
            }
          ],
          "partition-specs": [ { "spec-id": 0, "fields": [] } ],
          "snapshots": []
        }
        """;

        var tableId = new TableIdentifier("tenant-1", "raw", "orders");
        var catalogMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceName = "tenant-1",
                SchemaName = "raw",
                TableName = "orders",
                DataSourceType = DataSourceType.LakehouseIceberg,
                Location = "s3://lake/raw/orders/metadata/v2.metadata.json",
                IsActive = true
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "string" }]
        };

        bool allowAccess = true;
        var adp = new RecordingAccessDecisionPoint(req =>
            allowAccess
                ? TableAccessDecision.Allowed(
                    req.Object.Identifier,
                    new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["id"] = ColumnAccessLevel.Clear },
                    hasUnconstrainedColumnAllow: true)
                : TableAccessDecision.Denied(req.Object.Identifier, "No active grant for table."));

        var auditRepo = new RecordingAuditLogRepository();
        var service = new IcebergRestCatalogFederationService(
            new IcebergMetadataReader(new StubLakehouseStorageProvider(metadataJson), NullLogger<IcebergMetadataReader>.Instance),
            new InMemoryTableMetadataRepository(catalogMeta),
            Options.Create(new GatewayOptions { Lakehouse = new LakehouseOptions { Enabled = true } }),
            NullLogger<IcebergRestCatalogFederationService>.Instance,
            auditRepo,
            accessDecisionPoint: adp,
            transportGate: NSubstitute.Substitute.For<Autheris.Application.DataSources.IDataSourceTransportGate>());

        var principal = CreatePrincipal();

        // 1. Allowed path records AccessTransport.Lakehouse read decisions for both LoadTable and VendCredential
        await service.LoadTableAsync("tenant-1", "raw", "orders", principal);
        await Should.ThrowAsync<NotSupportedException>(() =>
            service.VendCredentialAsync("tenant-1", "raw", "orders", principal).AsTask());

        adp.Requests.Count.ShouldBe(2);
        adp.Requests[0].Transport.ShouldBe(Autheris.Application.Policy.Pdp.AccessTransport.Lakehouse);
        adp.Requests[0].Operation.ShouldBe(Autheris.Application.Policy.Pdp.AccessOperation.Read);
        adp.Requests[1].Transport.ShouldBe(Autheris.Application.Policy.Pdp.AccessTransport.Lakehouse);
        adp.Requests[1].Operation.ShouldBe(Autheris.Application.Policy.Pdp.AccessOperation.Read);

        // 2. Denied path throws SecurityException (403) and issues no credentials
        allowAccess = false;
        await Should.ThrowAsync<SecurityException>(() =>
            service.LoadTableAsync("tenant-1", "raw", "orders", principal).AsTask());
        await Should.ThrowAsync<SecurityException>(() =>
            service.VendCredentialAsync("tenant-1", "raw", "orders", principal).AsTask());

        adp.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Delta_joins_with_sql_server_source_via_federation()
    {
        var deltaTableId = new TableIdentifier("lake", "sales", "delta_orders");
        var deltaMetadata = new TableMetadata
        {
            Identifier = deltaTableId,
            Table = new Table { SourceName = "lake", SchemaName = "sales", TableName = "delta_orders", DataSourceType = DataSourceType.LakehouseDelta, IsActive = true },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "string" },
                new TableColumn { ColumnName = "customer_id", DataType = "string" },
                new TableColumn { ColumnName = "tenantId", DataType = "string" }
            ]
        };

        var sqlTableId = new TableIdentifier("erp", "dbo", "customers");
        var sqlMetadata = new TableMetadata
        {
            Identifier = sqlTableId,
            PrimaryKeyColumns = ["customer_id"],
            Table = new Table { SourceName = "erp", SchemaName = "dbo", TableName = "customers", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns =
            [
                new TableColumn { ColumnName = "customer_id", DataType = "string" },
                new TableColumn { ColumnName = "company_name", DataType = "string" },
                new TableColumn { ColumnName = "email", DataType = "string", IsSensitive = true },
                new TableColumn { ColumnName = "region", DataType = "string" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new MaskingRule { RuleType = "REDACT" }
            }
        };

        var snapshot = CreateDeltaSnapshot(
            "delta-join-1",
            ["id", "customer_id", "tenantId"],
            ["tenantId", "customer_id"],
            ("part-c10.parquet", new Dictionary<string, string> { ["tenantId"] = "tenant-1", ["customer_id"] = "C-10" }),
            ("part-c20.parquet", new Dictionary<string, string> { ["tenantId"] = "tenant-1", ["customer_id"] = "C-20" }));

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [deltaTableId] = TableAccessDecision.Allowed(
                deltaTableId,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["customer_id"] = ColumnAccessLevel.Clear,
                    ["tenantId"] = ColumnAccessLevel.Clear
                },
                hasUnconstrainedColumnAllow: true),
            [sqlTableId] = TableAccessDecision.Allowed(
                sqlTableId,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["customer_id"] = ColumnAccessLevel.Clear,
                    ["company_name"] = ColumnAccessLevel.Clear,
                    ["email"] = ColumnAccessLevel.Mask,
                    ["region"] = ColumnAccessLevel.Clear
                },
                rowFilterSql: "region = 'EU'")
        };

        var metadataRepo = new InMemoryTableMetadataRepository(deltaMetadata, sqlMetadata);
        var auditRepo = new RecordingAuditLogRepository();
        var deltaExecutor = new DeltaLakeDataSourceExecutor(
            new StubDeltaMetadataReader(snapshot),
            new DeltaPartitionPruner(NullLogger<DeltaPartitionPruner>.Instance),
            new RedactingMaskingProvider(),
            Options.Create(new GatewayOptions()),
            NullLogger<DeltaLakeDataSourceExecutor>.Instance,
            new DemoDataSwitch(true));

        var gatewayService = BuildGatewayExecutionService(metadataRepo, decisions, deltaExecutor, auditRepo);
        var principal = CreatePrincipal();

        var (deltaRows, _) = await gatewayService.ExecuteTableQueryAsync(
            principal, deltaTableId, first: 10, after: 0, queryArguments: null, requestedFields: ["id", "customer_id", "tenantId"]);
        deltaRows.Count.ShouldBe(2);

        var rowReader = new InMemoryCrossSourceRowReader();
        rowReader.SeedTable(sqlTableId,
        [
            new Dictionary<string, object?> { ["customer_id"] = "C-10", ["company_name"] = "Alpine GmbH", ["email"] = "contact@alpine.de", ["region"] = "EU" },
            new Dictionary<string, object?> { ["customer_id"] = "C-20", ["company_name"] = "Pacific Inc", ["email"] = "info@pacific.us", ["region"] = "US" }
        ]);

        var relationResolver = new CrossSourceRelationResolver(
            metadataRepo,
            new FixedUnifiedPolicyDecisionPoint(decisions),
            rowReader,
            new RedactingMaskingProvider(),
            auditRepo);

        var relation = new TableRelation
        {
            ParentTableIdentifier = deltaTableId,
            ChildTableIdentifier = sqlTableId,
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["customer_id"],
            Cardinality = RelationCardinality.ManyToOne,
            AllowCrossDataSource = true
        };

        var joinResult = await relationResolver.ResolveRelationAsync(
            new CrossSourceRelationRequest(relation, deltaRows, principal, DefaultTenant));

        joinResult.TargetRowsMaterialized.ShouldBe(1);
        joinResult.ManyToOneByParentIndex[0].ShouldNotBeNull();
        joinResult.ManyToOneByParentIndex[0]!["company_name"].ShouldBe("Alpine GmbH");
        joinResult.ManyToOneByParentIndex[0]!["email"].ShouldBe("[REDACTED]");
        joinResult.ManyToOneByParentIndex[1].ShouldBeNull("US customer row must be filtered out by SQL Server target RLS.");
    }

    [Fact]
    public async Task Delta_query_is_audited()
    {
        var tableId = new TableIdentifier("lake", "sales", "delta_orders");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "lake", SchemaName = "sales", TableName = "delta_orders", DataSourceType = DataSourceType.LakehouseDelta, IsActive = true },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "string" },
                new TableColumn { ColumnName = "tenantId", DataType = "string" }
            ]
        };

        var snapshot = CreateDeltaSnapshot(
            "delta-audit-1",
            ["id", "tenantId"],
            ["tenantId"],
            ("part-0.parquet", new Dictionary<string, string> { ["tenantId"] = "tenant-1" }));

        var decision = TableAccessDecision.Allowed(
            tableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["tenantId"] = ColumnAccessLevel.Clear
            });

        var auditRepo = new RecordingAuditLogRepository();
        var deltaExecutor = new DeltaLakeDataSourceExecutor(
            new StubDeltaMetadataReader(snapshot),
            new DeltaPartitionPruner(NullLogger<DeltaPartitionPruner>.Instance),
            new RedactingMaskingProvider(),
            Options.Create(new GatewayOptions()),
            NullLogger<DeltaLakeDataSourceExecutor>.Instance,
            new DemoDataSwitch(true));

        var service = BuildGatewayExecutionService(
            new InMemoryTableMetadataRepository(metadata),
            new Dictionary<TableIdentifier, TableAccessDecision> { [tableId] = decision },
            deltaExecutor,
            auditRepo);

        var (rows, _) = await service.ExecuteTableQueryAsync(
            CreatePrincipal("S-1-5-21-AUDIT-USER", "tenant-1"),
            tableId,
            first: 5,
            after: 0,
            queryArguments: null,
            requestedFields: ["id", "tenantId"]);

        rows.Count.ShouldBe(1);
        auditRepo.Entries.Count.ShouldBe(1);
        auditRepo.Entries[0].Decision.ShouldBe("ALLOW");
        auditRepo.Entries[0].TenantId.ShouldBe(new TenantId("tenant-1"));
        auditRepo.Entries[0].ActorSid.ShouldBe(new Sid("S-1-5-21-AUDIT-USER"));
        auditRepo.Entries[0].TargetTable.ShouldContain("delta_orders");
    }
private sealed class StubDeltaMetadataReader(DeltaSnapshot snapshot) : IDeltaMetadataReader
    {
        public ValueTask<DeltaSnapshot> LoadSnapshotAsync(
            string tableLocation,
            long? asOfVersion = null,
            DateTimeOffset? asOfTimestamp = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<IReadOnlyList<DeltaDataFile>> LoadActiveFilesAsync(
            string tableLocation,
            long? asOfVersion = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<DeltaDataFile>>(snapshot.ActiveFiles);
    }

    private sealed class RedactingMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? rawValue, MaskingRule rule)
            => rawValue is null ? null : (rule.Replacement ?? "[REDACTED]");
    }

    private sealed class InMemoryTableMetadataRepository : ITableMetadataRepository
    {
        private readonly Dictionary<TableIdentifier, TableMetadata> _tables = new();

        public InMemoryTableMetadataRepository(params TableMetadata[] tables)
        {
            foreach (var table in tables)
            {
                _tables[table.Identifier] = table;
            }
        }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(_tables.TryGetValue(table, out var meta) ? meta : null);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TableMetadata>>(_tables.Values.ToList());

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            _tables[metadata.Identifier] = metadata;
            return Task.FromResult(metadata);
        }

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default)
            => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class EmptyConsentRepository : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());

        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
            => Task.FromResult(consent);

        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default)
            => Task.FromResult<Consent?>(null);

        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());

        public Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());

        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FixedDecisionConsentResolutionService(Dictionary<TableIdentifier, TableAccessDecision> decisions) : IConsentResolutionService
    {
        public TableAccessDecision ResolveAccess(
            Sid userSid,
            IReadOnlySet<Sid> subjectGroupSids,
            IReadOnlySet<string> userRoles,
            TableIdentifier table,
            IReadOnlyList<Consent> activeConsents,
            DatabaseDialect dialect = DatabaseDialect.SqlServer,
            IReadOnlySet<Sid>? allUserSids = null,
            IReadOnlySet<string>? callerScopes = null)
            => decisions.TryGetValue(table, out var decision)
                ? decision
                : TableAccessDecision.Denied(table, "No decision configured.");
    }

    private sealed class PassThroughConsentCacheService : IConsentCacheService
    {
        public Task<TableAccessDecision?> GetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, string? contextHash = null, CancellationToken ct = default)
            => Task.FromResult<TableAccessDecision?>(null);

        public Task<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>> GetCachedDecisionsAsync(TenantId tenant, Sid userSid, IReadOnlyList<TableIdentifier> tables, string? contextHash = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>>(new Dictionary<TableIdentifier, TableAccessDecision?>());

        public Task SetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, string? contextHash = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task EvictTableDecisionsAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ClearL1CacheAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class AllowingPolicyEnforcementService : IPolicyEnforcementService
    {
        public ValueTask<TableAccessDecision> EvaluatePolicyAsync(SecurityEvaluationContext context, CancellationToken ct = default)
        {
            var cols = context.RequestedColumns.ToDictionary(c => c, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase);
            return ValueTask.FromResult(TableAccessDecision.Allowed(context.TargetTable, cols, hasUnconstrainedColumnAllow: true));
        }

        public bool HasPolicies(TenantId tenant) => false;

        public Task ReloadPoliciesAsync(TenantId tenant, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class AllowingTransportGate : IDataSourceTransportGate
    {
        public DataSourceTransportFlags Ingress => DataSourceTransportFlags.GraphQL;
        public IReadOnlyList<string> CheckedSources => [];
        public Task EnsureAsync(string? dataSourceName, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureAllAsync(IEnumerable<string?> dataSourceNames, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureCatalogSourcesAsync(IEnumerable<string?> catalogSourceNames, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureCatalogSourceAsync(string? catalogSourceName, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsAllowed(string? dataSourceName) => true;
    }

    private sealed class RecordingAuditLogRepository : IAuditLogRepository
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

        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
            string? targetTable = null,
            Sid? actorSid = null,
            DateTimeOffset? since = null,
            int limit = 1000,
            TenantId? tenantId = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class FixedUnifiedPolicyDecisionPoint(Dictionary<TableIdentifier, TableAccessDecision> decisions) : IUnifiedPolicyDecisionPoint
    {
        public Task<TableAccessDecision> EvaluateAccessAsync(
            TableIdentifier table,
            TableMetadata metadata,
            SecurityPrincipalContext securityContext,
            IReadOnlyList<string>? requestedColumns,
            CancellationToken ct)
            => Task.FromResult(decisions.TryGetValue(table, out var decision)
                ? decision
                : TableAccessDecision.Denied(table, "Denied"));
    }

    private sealed class StubLakehouseStorageProvider(string metadataJson) : ILakehouseStorageProvider
    {
        public ValueTask<string> ReadTextAsync(string location, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(metadataJson);

        public ValueTask<Stream> OpenReadStreamAsync(string location, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Stream>(new MemoryStream());

        public ValueTask<bool> ExistsAsync(string location, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }

    private sealed class RecordingAccessDecisionPoint(Func<Autheris.Application.Policy.Pdp.AccessRequest, TableAccessDecision> handler) : Autheris.Application.Policy.Pdp.IAccessDecisionPoint
    {
        public List<Autheris.Application.Policy.Pdp.AccessRequest> Requests { get; } = [];

        public ValueTask<TableAccessDecision> DecideAsync(Autheris.Application.Policy.Pdp.AccessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(handler(request));
        }

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
            Autheris.Domain.Security.SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, Autheris.Application.Policy.Pdp.AccessOperation operation, Autheris.Application.Policy.Pdp.AccessTransport transport, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<Autheris.Application.Policy.Pdp.AccessExplanation> ExplainAsync(Autheris.Application.Policy.Pdp.AccessRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static GatewayExecutionService BuildGatewayExecutionService(
        ITableMetadataRepository metadataRepo,
        Dictionary<TableIdentifier, TableAccessDecision> decisions,
        IDataSourceExecutor deltaExecutor,
        RecordingAuditLogRepository auditRepo) =>
        new(
            metadataRepository: metadataRepo,
            consentRepository: new EmptyConsentRepository(),
            auditLogRepository: auditRepo,
            resolutionService: new FixedDecisionConsentResolutionService(decisions),
            cacheService: new PassThroughConsentCacheService(),
            maskingProvider: new RedactingMaskingProvider(),
            options: Options.Create(new GatewayOptions()),
            dataSourceExecutors: [deltaExecutor],
            policyEnforcementService: new AllowingPolicyEnforcementService(),
            transportGate: new AllowingTransportGate());

    private static DeltaSnapshot CreateDeltaSnapshot(
        string id,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> partitionColumns,
        params (string Path, Dictionary<string, string> Partitions)[] files) =>
        new(
            TableLocation: "delta_orders",
            Version: 1,
            TimestampMs: 1700000000000,
            Metadata: new DeltaTableMetadata(
                Id: id,
                Name: "delta_orders",
                Description: null,
                FormatProvider: "parquet",
                Schema: new DeltaSchema("struct", columns.Select(c => new DeltaField(c, "string")).ToArray()),
                PartitionColumns: partitionColumns,
                CreatedTimeMs: 1700000000000,
                Configuration: new Dictionary<string, string>()),
            ActiveFiles: files.Select(f => new DeltaDataFile(
                Path: f.Path,
                PartitionValues: f.Partitions,
                SizeBytes: 512,
                ModificationTimeMs: 1700000000000,
                DataChange: true,
                RecordCount: 1,
                MinValues: f.Partitions,
                MaxValues: f.Partitions)).ToArray(),
            Protocol: new DeltaProtocol(1, 2));

    
}
