namespace Autheris.Tests.Integration.Federation;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.DataSources;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Services;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Infrastructure.Connectors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

[Trait("Category", "Security")]
public sealed class FederationSecurityTests
{
    private static readonly TableIdentifier CrmOrdersTable = new("crm", "public", "orders");
    private static readonly TableIdentifier BillingCustomersTable = new("billing", "public", "customers");

    [Fact]
    [Trait("Category", "Security")]
    public async Task Cross_source_join_applies_per_source_decision_masking_and_tenant_isolation_in_staging()
    {
        var ordersMeta = new TableMetadata
        {
            Identifier = CrmOrdersTable,
            Table = new Table
            {
                TableName = "orders",
                SchemaName = "public",
                SourceName = "crm",
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "varchar" },
                new TableColumn { ColumnName = "customer_id", DataType = "varchar" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "secret_note", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["secret_note"] = new MaskingRule { RuleType = "REDACT" }
            }
        };

        var customersMeta = new TableMetadata
        {
            Identifier = BillingCustomersTable,
            Table = new Table
            {
                TableName = "customers",
                SchemaName = "public",
                SourceName = "billing",
                SourceType = "SqlServer",
                DataSourceType = DataSourceType.Sql,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "varchar" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "email", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new MaskingRule { RuleType = "MASK_EMAIL" }
            }
        };

        var orderRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = "10", ["customer_id"] = "101", ["tenant_id"] = "tenant-a", ["secret_note"] = "TOP_SECRET_A" },
            new Dictionary<string, object?> { ["id"] = "20", ["customer_id"] = "202", ["tenant_id"] = "tenant-b", ["secret_note"] = "TOP_SECRET_B" }
        };

        var customerRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = "101", ["tenant_id"] = "tenant-a", ["email"] = "alice@corp.example" },
            new Dictionary<string, object?> { ["id"] = "202", ["tenant_id"] = "tenant-b", ["email"] = "bob@other.example" }
        };

        var registry = new InMemoryConnectorRegistry();
        registry.RegisterConnector("crm", new InMemorySourceConnector("crm", ordersMeta, orderRows));
        registry.RegisterConnector("billing", new InMemorySourceConnector("billing", customersMeta, customerRows));

        var gatewayOptions = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                AllowedDataSources = ["crm", "billing"],
                CrossSource = new CrossSourceOptions { Enabled = true }
            },
            DuckDbOlap = new DuckDbOlapOptions
            {
                Enabled = true,
                MaxMemory = "256MB",
                MaxStagedRowsPerTable = 100,
                QueryTimeoutSeconds = 10,
                MaxThreads = 1
            }
        });

        var auditRepo = new RecordingAuditLogRepository();
        var stagingService = new FederatedStagingService(
            registry,
            new ColumnMaskingProvider(),
            auditRepo,
            gatewayOptions,
            NullLogger<FederatedStagingService>.Instance,
            transportGate: AllowWebSqlTransportGate.Instance);

        var sql = "SELECT o.id, o.secret_note, c.email FROM crm.public.orders o JOIN billing.public.customers c ON o.customer_id = c.id ORDER BY o.id";
        var analysis = FastSqlEngine.Default.Analyze(sql.AsMemory());

        var tOrders = new ResolvedSourceTable(analysis.ReferencedTables[0], ordersMeta.Identifier, ordersMeta, "crm");
        var tCustomers = new ResolvedSourceTable(analysis.ReferencedTables[1], customersMeta.Identifier, customersMeta, "billing");

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [ordersMeta.Identifier] = TableAccessDecision.Allowed(
                ordersMeta.Identifier,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["customer_id"] = ColumnAccessLevel.Clear,
                    ["tenant_id"] = ColumnAccessLevel.Clear,
                    ["secret_note"] = ColumnAccessLevel.Mask
                },
                rowFilterSql: "tenant_id = 'tenant-a'",
                hasUnconstrainedColumnAllow: false),
            [customersMeta.Identifier] = TableAccessDecision.Allowed(
                customersMeta.Identifier,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["tenant_id"] = ColumnAccessLevel.Clear,
                    ["email"] = ColumnAccessLevel.Mask
                },
                rowFilterSql: "tenant_id = 'tenant-a'",
                hasUnconstrainedColumnAllow: false)
        };

        var userA = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ALICE"),
            new Claim("tenant_id", "tenant-a")
        ], "Bearer"));
        var tenantA = new TenantId("tenant-a");

        var planner = new CrossSourcePlanner(FastSqlEngine.Default);
        var plan = planner.Plan(
            sql,
            analysis,
            [tOrders, tCustomers],
            decisions,
            new CrossSourceOptions { Enabled = true },
            userA,
            tenantA);

        var budget = new FederationBudget(
            MaxTableCount: 5,
            MaxStagedRowsPerTable: 100,
            MaxTotalStagedRows: 200,
            MaxStagedBytesPerTable: 1024 * 1024,
            MaxTotalStagedBytes: 2 * 1024 * 1024,
            TimeoutSeconds: 10,
            MaxParallelSourceReads: 2,
            AuditCorrelationId: "trace-fed-sec");

        var stagedSources = await stagingService.StageAsync(plan.StagingRequests, userA, tenantA, budget);
        stagedSources.Count.ShouldBe(2);

        // Verify Tenant B rows are excluded before DuckDB join and per-source masking is applied in staging.
        stagedSources[0].GovernedRows.Count.ShouldBe(1);
        stagedSources[0].GovernedRows[0]["secret_note"]?.ToString().ShouldBe("***");
        stagedSources[1].GovernedRows.Count.ShouldBe(1);
        stagedSources[1].GovernedRows[0]["email"]?.ToString().ShouldBe("***@***");

        using var engine = new DuckDbOlapEngine(gatewayOptions.Value.DuckDbOlap, NullLogger<DuckDbOlapEngine>.Instance);
        var queryResult = await engine.ExecuteGeneratedAsync(
            new GeneratedOlapQuery(plan.GeneratedDuckDbSql, plan.Compiled.Parameters, plan.Compiled),
            stagedSources);

        queryResult.Rows.Count.ShouldBe(1);
        queryResult.Rows[0][0]?.ToString().ShouldBe("10");
        queryResult.Rows[0][1]?.ToString().ShouldBe("***");
        queryResult.Rows[0][2]?.ToString().ShouldBe("***@***");

        // Fail-closed: if either source in the cross-source request is denied, StageAsync rejects before staging rows.
        var deniedRequests = plan.StagingRequests
            .Select((r, idx) => idx == 1 ? r with { Decision = TableAccessDecision.Denied(customersMeta.Identifier, "Missing consent") } : r)
            .ToList();

        await Should.ThrowAsync<GatewaySecurityException>(() =>
            stagingService.StageAsync(deniedRequests, userA, tenantA, budget));
    }

    private sealed class AllowWebSqlTransportGate : IDataSourceTransportGate
    {
        public static AllowWebSqlTransportGate Instance { get; } = new();

        public DataSourceTransportFlags Ingress => DataSourceTransportFlags.WebSql;

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

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class InMemorySourceConnector(
        string connectorId,
        TableMetadata tableMetadata,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IAutherisConnector, IConnectorMetadata, IConnectorSplitManager, IConnectorRecordSource
    {
        public string ConnectorId => connectorId;
        public string ConnectorType => "PostgreSql";
        public ConnectorCapabilities Capabilities { get; } = new(ConnectorFeatures.None);
        public IConnectorMetadata Metadata => this;
        public IConnectorSplitManager SplitManager => this;
        public IConnectorRecordSource RecordSource => this;

        public Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["public"]);

        public Task<IReadOnlyList<TableIdentifier>> ListTablesAsync(string? schema = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableIdentifier>>([tableMetadata.Identifier]);

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(tableMetadata);

        public Task<IReadOnlyList<ConnectorSplit>> GetSplitsAsync(TableMetadata table, ConnectorSessionContext session, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConnectorSplit>>([new ConnectorSplit("split-1", new Dictionary<string, object?>())]);

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadSplitAsync(
            ConnectorSplit split,
            ConnectorSessionContext session,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                yield return row;
            }
            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadBatchAsync(
            ConnectorSplit split,
            ConnectorSessionContext session,
            CancellationToken ct = default) =>
            Task.FromResult(rows);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
