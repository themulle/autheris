namespace Autheris.Tests.Unit.Analytics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Application.Analytics;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GovernedAggregationServiceSecurityTests
{
    private static readonly TableIdentifier OrdersTableId = new("sales", "public", "orders");

    private static TableMetadata CreateOrdersMetadata()
    {
        var tableId = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = OrdersTableId,
            Table = new Table
            {
                Id = tableId,
                SourceType = "PostgreSQL",
                SourceName = "default",
                SchemaName = "public",
                TableName = "orders",
                DataSourceType = DataSourceType.Sql,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int" },
                new TableColumn { TableId = tableId, ColumnName = "department", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "amount", DataType = "decimal" }
            ]
        };
    }

    private static TableAccessDecision CreateDecision() =>
        TableAccessDecision.Allowed(
            OrdersTableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["department"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Clear
            });

    private static ClaimsPrincipal CreatePrincipal() =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ANALYST"),
            new Claim(ClaimTypes.Name, "analyst"),
            new Claim(ClaimTypes.Role, "Analyst"),
            new Claim("tenant_id", "tenant-a")
        ], "TestAuth"));

    private static List<IReadOnlyDictionary<string, object?>> BuildRows()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (int i = 0; i < 6; i++)
        {
            rows.Add(new Dictionary<string, object?> { ["department"] = "Eng", ["amount"] = 200m });
        }

        rows.Add(new Dictionary<string, object?> { ["department"] = "Executive", ["amount"] = 500m });
        rows.Add(new Dictionary<string, object?> { ["department"] = "Finance", ["amount"] = 100m });
        for (int i = 0; i < 5; i++)
        {
            rows.Add(new Dictionary<string, object?> { ["department"] = "Sales", ["amount"] = 150m });
        }

        return rows;
    }

    private static (GovernedAggregationService Service, CapturingAuditLogRepository Audit) CreateService(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        AnalyticsOptions? options = null)
    {
        var metaRepo = new StubTableMetadataRepository(CreateOrdersMetadata());
        var executor = new StubRowExecutor(rows);
        var audit = new CapturingAuditLogRepository();
        var service = new GovernedAggregationService(
            metaRepo,
            audit,
            Autheris.Tests.Unit.TestSupport.AccessDecisionPointStubs.Denying(),
            dataSourceExecutors: [executor],
            analyticsOptions: Options.Create(options ?? new AnalyticsOptions()));
        return (service, audit);
    }

    [Fact]
    public async Task Suppressed_groups_count_hidden_by_default()
    {
        var defaultPrivacy = new AnalyticsPrivacyOptions();
        defaultPrivacy.ExposeSuppressedGroupsCount.ShouldBeFalse();

        var (service, audit) = CreateService(BuildRows());
        var result = await service.ExecuteAggregationAsync(
            CreatePrincipal(),
            new AggregationQuery(
                OrdersTableId,
                GroupBy: ["department"],
                PreEvaluatedDecision: CreateDecision().AsPdpEvaluated()));

        result.Suppressed.ShouldBeTrue();
        result.SuppressedGroupsCount.ShouldBe(0);
        result.SuppressedRowsCount.ShouldBe(0);

        using var details = JsonDocument.Parse(audit.Entries.Last().DetailsJson!);
        details.RootElement.GetProperty("exposed_suppressed_counts").GetBoolean().ShouldBeFalse();
        details.RootElement.GetProperty("suppressed_groups_count").GetInt32().ShouldBeGreaterThan(0);
        details.RootElement.GetProperty("suppressed_rows_count").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Audit_contains_filter_shape_without_literals_and_exposed_flag()
    {
        var (serviceDefault, auditDefault) = CreateService(BuildRows());
        var whereClause = TableFilterClause.FromTrinoText(
            "amount > 123 AND department = 'SensitiveLiteralDept'",
            ["amount", "department"]);

        await serviceDefault.ExecuteAggregationAsync(
            CreatePrincipal(),
            new AggregationQuery(
                OrdersTableId,
                GroupBy: ["department"],
                Where: whereClause,
                PreEvaluatedDecision: CreateDecision().AsPdpEvaluated()));

        using (var doc = JsonDocument.Parse(auditDefault.Entries.Last().DetailsJson!))
        {
            doc.RootElement.TryGetProperty("noise_applied", out _).ShouldBeFalse();
            doc.RootElement.GetProperty("exposed_suppressed_counts").GetBoolean().ShouldBeFalse();

            var shape = doc.RootElement.GetProperty("filter_shape")
                .EnumerateArray()
                .Select(e => e.GetString()!)
                .ToArray();
            shape.ShouldBe(["amount:gt", "department:eq"]);
            string.Join(",", shape).ShouldNotContain("SensitiveLiteralDept");
            string.Join(",", shape).ShouldNotContain("123");
        }

        var (serviceExposed, auditExposed) = CreateService(
            BuildRows(),
            new AnalyticsOptions
            {
                Privacy = new AnalyticsPrivacyOptions
                {
                    DefaultMinGroupSize = 5,
                    ExposeSuppressedGroupsCount = true
                }
            });

        var pushdownWhere = TableFilterClause.FromTrinoText(
            "department = 'Executive' AND amount >= 100",
            ["department", "amount"]) with
        {
            PushdownPredicates =
            [
                new PushdownPredicateInfo("department", "Equal"),
                new PushdownPredicateInfo("amount", "GreaterThanOrEqual")
            ]
        };

        var exposedResult = await serviceExposed.ExecuteAggregationAsync(
            CreatePrincipal(),
            new AggregationQuery(
                OrdersTableId,
                GroupBy: ["department"],
                Where: pushdownWhere,
                PreEvaluatedDecision: CreateDecision().AsPdpEvaluated()));

        exposedResult.SuppressedGroupsCount.ShouldBeGreaterThan(0);
        using var exposedDoc = JsonDocument.Parse(auditExposed.Entries.Last().DetailsJson!);
        exposedDoc.RootElement.GetProperty("exposed_suppressed_counts").GetBoolean().ShouldBeTrue();
        var exposedShape = exposedDoc.RootElement.GetProperty("filter_shape")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
        exposedShape.ShouldBe(["amount:gte", "department:eq"]);
        string.Join(",", exposedShape).ShouldNotContain("Executive");
        string.Join(",", exposedShape).ShouldNotContain("100");
    }

    [Fact]
    public void Differential_privacy_leftovers_are_removed()
    {
        typeof(AggregationMetadata).GetProperty("NoiseApplied").ShouldBeNull();
        typeof(AggregationMetadata).GetProperty("EpsilonConsumed").ShouldBeNull();
        typeof(AggregationMetadata).GetProperty("RemainingEpsilon").ShouldBeNull();
        typeof(AggregationResult).GetProperty("NoiseApplied").ShouldBeNull();
        typeof(GovernedAggregationService).Assembly.GetType("Autheris.Application.Analytics.NoisyCacheKey").ShouldBeNull();
        typeof(ApiErrorCodes).GetField("PrivacyBudgetExhausted").ShouldBeNull();
    }

    private sealed class StubTableMetadataRepository(TableMetadata metadata) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(table == metadata.Identifier ? metadata : null);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>([metadata]);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata updated, CancellationToken ct = default) =>
            Task.FromResult(updated);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class StubRowExecutor(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IDataSourceExecutor
    {
        public DataSourceType SupportedType => DataSourceType.Sql;

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
            DataSourceExecutionContext context,
            CancellationToken ct = default) =>
            Task.FromResult(rows);
    }

    private sealed class CapturingAuditLogRepository : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default)
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
}
