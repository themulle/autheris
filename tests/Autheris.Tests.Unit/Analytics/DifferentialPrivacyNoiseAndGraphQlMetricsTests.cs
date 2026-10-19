namespace Autheris.Tests.Unit.Analytics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Services;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DifferentialPrivacyNoiseAndGraphQlMetricsTests
{
    private static readonly TableIdentifier OrdersTableId = new("sales", "public", "orders");

    private static TableMetadata CreateOrdersMetadata(int? minGroupSize = 2)
    {
        var tableId = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = OrdersTableId,
            MinGroupSize = minGroupSize,
            Table = new Table
            {
                Id = tableId,
                SourceType = "PostgreSQL",
                SourceName = "default",
                SchemaName = "public",
                TableName = "orders",
                DataSourceType = DataSourceType.Sql,
                IsActive = true,
                MinGroupSize = minGroupSize
            },
            Columns =
            [
                new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int" },
                new TableColumn { TableId = tableId, ColumnName = "region", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "amount", DataType = "decimal" }
            ]
        };
    }

    private static ClaimsPrincipal CreatePrincipal(string sid, string tenant = "tenant-a", string role = "Analyst") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, sid),
            new Claim(ClaimTypes.Name, sid),
            new Claim(ClaimTypes.Role, role),
            new Claim("tenant_id", tenant)
        ], "TestAuth"));

    private sealed class InMemoryRowExecutor(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IDataSourceExecutor
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

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> CreateSampleRows() =>
    [
        new Dictionary<string, object?> { ["id"] = 1, ["region"] = "EU", ["amount"] = 100m },
        new Dictionary<string, object?> { ["id"] = 2, ["region"] = "EU", ["amount"] = 200m },
        new Dictionary<string, object?> { ["id"] = 3, ["region"] = "EU", ["amount"] = 300m },
        new Dictionary<string, object?> { ["id"] = 4, ["region"] = "US", ["amount"] = 400m },
        new Dictionary<string, object?> { ["id"] = 5, ["region"] = "US", ["amount"] = 500m },
        new Dictionary<string, object?> { ["id"] = 6, ["region"] = "US", ["amount"] = 600m }
    ];

    [Fact]
    public async Task ExecuteAggregationAsync_WhenNoiseEnabled_PerturbsCountSumAvgAndDeductsBudget()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>())
            .Returns(CreateOrdersMetadata(minGroupSize: 2));

        var auditRepo = new CapturingAuditLogRepository();
        var options = Options.Create(new AnalyticsOptions
        {
            Privacy = new AnalyticsPrivacyOptions
            {
                DefaultMinGroupSize = 2,
                Noise = new AnalyticsNoiseOptions
                {
                    Enabled = true,
                    Epsilon = 1.5,
                    DailyBudgetEpsilon = 10.0,
                    MeasureBounds = new Dictionary<string, MeasureBoundsOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["amount"] = new MeasureBoundsOptions { Min = 0, Max = 1000 }
                    }
                }
            }
        });

        var dpEngine = new DifferentialPrivacyEngine(auditRepo, analyticsOptions: options);
        var service = new GovernedAggregationService(
            metadataRepo,
            auditRepo,
            dataSourceExecutors: [new InMemoryRowExecutor(CreateSampleRows())],
            analyticsOptions: options,
            dpEngine: dpEngine);

        var principal = CreatePrincipal("S-1-5-21-DP-USER-1");
        var query = new AggregationQuery(
            Table: OrdersTableId,
            GroupBy: ["region"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Count, null, "cnt"),
                new AggregateSelection(AggregateFunction.Sum, "amount", "sum_amount"),
                new AggregateSelection(AggregateFunction.Avg, "amount", "avg_amount")
            ],
            BypassTableConsent: true);

        var result = await service.ExecuteAggregationAsync(principal, query);

        result.NoiseApplied.ShouldBeTrue();
        result.Metadata.NoiseApplied.ShouldBeTrue();
        // WP-0 (SEC-112-03): two column aggregates (sum, avg) plus the row count are noised, so 3 x 1.5 is charged.
        result.Metadata.EpsilonConsumed.ShouldBe(4.5);
        result.Metadata.RemainingEpsilon.ShouldBe(5.5);
        result.Groups.Count.ShouldBe(2);

        var eu = result.Groups.Single(g => Equals(g.GroupKey["region"], "EU"));
        eu.Count.ShouldNotBe(3L);
        Convert.ToDecimal(eu.Aggregates["sum_amount"]).ShouldNotBe(600m);
        Convert.ToDecimal(eu.Aggregates["avg_amount"]).ShouldNotBe(200m);
    }

    [Fact]
    public async Task ExecuteAggregationAsync_RepeatingIdenticalQueryOnSameDay_ReturnsCachedNoisyResultWithoutExtraBudgetDeduction()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>())
            .Returns(CreateOrdersMetadata(minGroupSize: 2));

        var auditRepo = new CapturingAuditLogRepository();
        var options = Options.Create(new AnalyticsOptions
        {
            Privacy = new AnalyticsPrivacyOptions
            {
                DefaultMinGroupSize = 2,
                Noise = new AnalyticsNoiseOptions
                {
                    Enabled = true,
                    Epsilon = 2.0,
                    DailyBudgetEpsilon = 6.0,
                    MeasureBounds = new Dictionary<string, MeasureBoundsOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["orders.amount"] = new MeasureBoundsOptions { Min = 0, Max = 1000 }
                    }
                }
            }
        });

        var dpEngine = new DifferentialPrivacyEngine(auditRepo, analyticsOptions: options);
        var service = new GovernedAggregationService(
            metadataRepo,
            auditRepo,
            dataSourceExecutors: [new InMemoryRowExecutor(CreateSampleRows())],
            analyticsOptions: options,
            dpEngine: dpEngine);

        var principal = CreatePrincipal("S-1-5-21-DP-CACHED");
        var query = new AggregationQuery(
            Table: OrdersTableId,
            GroupBy: ["region"],
            Aggregates: [new AggregateSelection(AggregateFunction.Sum, "amount", "sum_amount")],
            BypassTableConsent: true);

        var first = await service.ExecuteAggregationAsync(principal, query);
        var second = await service.ExecuteAggregationAsync(principal, query);

        second.Groups[0].Count.ShouldBe(first.Groups[0].Count);
        second.Groups[0].Aggregates["sum_amount"].ShouldBe(first.Groups[0].Aggregates["sum_amount"]);

        var budget = await dpEngine.GetBudgetAsync("S-1-5-21-DP-CACHED");
        // One column aggregate plus the row count: 2 x 2.0, charged once (the second call is a cache hit).
        budget.ConsumedEpsilon.ShouldBe(4.0);
        budget.RemainingEpsilon.ShouldBe(2.0);
    }

    [Fact]
    public async Task ExecuteAggregationAsync_ExceedingDailyBudget_ThrowsPrivacyBudgetExhaustedException()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>())
            .Returns(CreateOrdersMetadata(minGroupSize: 2));

        var auditRepo = new CapturingAuditLogRepository();
        var options = Options.Create(new AnalyticsOptions
        {
            Privacy = new AnalyticsPrivacyOptions
            {
                DefaultMinGroupSize = 2,
                Noise = new AnalyticsNoiseOptions
                {
                    Enabled = true,
                    Epsilon = 2.0,
                    DailyBudgetEpsilon = 3.0
                }
            }
        });

        var dpEngine = new DifferentialPrivacyEngine(auditRepo, analyticsOptions: options);
        var service = new GovernedAggregationService(
            metadataRepo,
            auditRepo,
            dataSourceExecutors: [new InMemoryRowExecutor(CreateSampleRows())],
            analyticsOptions: options,
            dpEngine: dpEngine);

        var principal = CreatePrincipal("S-1-5-21-DP-EXHAUST");

        await service.ExecuteAggregationAsync(principal, new AggregationQuery(
            Table: OrdersTableId,
            GroupBy: ["region"],
            Aggregates: [new AggregateSelection(AggregateFunction.Count)],
            Limit: 10,
            BypassTableConsent: true));

        await Should.ThrowAsync<PrivacyBudgetExhaustedException>(() =>
            service.ExecuteAggregationAsync(principal, new AggregationQuery(
                Table: OrdersTableId,
                GroupBy: ["region"],
                Aggregates: [new AggregateSelection(AggregateFunction.Count)],
                Limit: 20,
                BypassTableConsent: true)));
    }

    [Fact]
    public async Task ExecuteAggregationAsync_SumWithoutMeasureBoundsWhenNoiseEnabled_ThrowsGatewayInvalidQueryException()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>())
            .Returns(CreateOrdersMetadata(minGroupSize: 2));

        var auditRepo = new CapturingAuditLogRepository();
        var options = Options.Create(new AnalyticsOptions
        {
            Privacy = new AnalyticsPrivacyOptions
            {
                DefaultMinGroupSize = 2,
                Noise = new AnalyticsNoiseOptions
                {
                    Enabled = true,
                    Epsilon = 1.0,
                    DailyBudgetEpsilon = 10.0
                }
            }
        });

        var service = new GovernedAggregationService(
            metadataRepo,
            auditRepo,
            dataSourceExecutors: [new InMemoryRowExecutor(CreateSampleRows())],
            analyticsOptions: options);

        var principal = CreatePrincipal("S-1-5-21-DP-NOBOUNDS");
        var query = new AggregationQuery(
            Table: OrdersTableId,
            GroupBy: ["region"],
            Aggregates: [new AggregateSelection(AggregateFunction.Sum, "amount", "sum_amount")],
            BypassTableConsent: true);

        await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            service.ExecuteAggregationAsync(principal, query));
    }

    [Fact]
    public async Task QueryMetrics_ExecutesGovernedMetricQueryForAuthenticatedUser_ThroughTheTypedField()
    {
        var principal = CreatePrincipal("S-1-5-21-GQL-ANALYST");
        var host = new Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost { User = principal };

        var expectedResult = new MetricQueryResult(
            Metrics: ["total_revenue"],
            ManifestVersion: "v1",
            Groups: [new AggregationGroupRow(new Dictionary<string, object?> { ["region"] = "EU" }, 5, new Dictionary<string, object?> { ["total_revenue"] = 1000m })],
            Totals: new AggregationGroupRow(new Dictionary<string, object?>(), 5, new Dictionary<string, object?> { ["total_revenue"] = 1000m }),
            Metadata: new AggregationMetadata(2, SmallGroupStrategy.Suppress, false, 0, 0, false, false, NoiseApplied: true, EpsilonConsumed: 1.0, RemainingEpsilon: 9.0),
            MetricDefinitions: new Dictionary<string, Autheris.Application.Dbt.Services.MetricDefinitionSummary>())
        {
            MetricValueKinds = new Dictionary<string, AggregateValueKind> { ["total_revenue"] = AggregateValueKind.Decimal },
            DimensionValueKinds = new Dictionary<string, AggregateValueKind> { ["region"] = AggregateValueKind.String }
        };

        MetricQueryRequest? captured = null;
        host.Metrics.QueryMetricsAsync(principal, Arg.Do<MetricQueryRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(expectedResult);
        var executor = await host.BuildAsync();

        var response = await executor.ExecuteAsync(
            "{ queryMetrics(metrics: [\"total_revenue\"], groupBy: [\"region\"], timeGranularity: \"month\", orderBy: [{ metric: \"total_revenue\", direction: DESC }], limit: 25) " +
            "{ noiseApplied groups { values { alias decimalValue } } } }");

        Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.Errors(response).ShouldBeEmpty();
        captured!.Metrics.ShouldBe(["total_revenue"]);
        captured.GroupBy.ShouldBe(["region"]);
        captured.TimeGranularity.ShouldBe("month");
        captured.Limit.ShouldBe(25);
        captured.OrderBy.ShouldBe([new TableOrderBy("total_revenue", true)]);
        using var doc = Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.Json(response);
        var data = doc.RootElement.GetProperty("data").GetProperty("queryMetrics");
        data.GetProperty("noiseApplied").GetBoolean().ShouldBeTrue();
        data.GetProperty("groups")[0].GetProperty("values")[0].GetProperty("decimalValue").GetDecimal().ShouldBe(1000m);
    }

    [Fact]
    public void AnalyticsOptionsValidator_ValidatesNoiseSettings()
    {
        var validator = new AnalyticsOptionsValidator();

        var invalidEpsilon = new AnalyticsOptions
        {
            Privacy = new AnalyticsPrivacyOptions
            {
                Noise = new AnalyticsNoiseOptions { Enabled = true, Epsilon = 0, DailyBudgetEpsilon = 5.0 }
            }
        };
        validator.Validate(null, invalidEpsilon).Failed.ShouldBeTrue();

        var invalidBounds = new AnalyticsOptions
        {
            Privacy = new AnalyticsPrivacyOptions
            {
                Noise = new AnalyticsNoiseOptions
                {
                    Enabled = true,
                    Epsilon = 1.0,
                    DailyBudgetEpsilon = 5.0,
                    MeasureBounds = new Dictionary<string, MeasureBoundsOptions>
                    {
                        ["amount"] = new MeasureBoundsOptions { Min = 100, Max = 50 }
                    }
                }
            }
        };
        validator.Validate(null, invalidBounds).Failed.ShouldBeTrue();
    }
}
