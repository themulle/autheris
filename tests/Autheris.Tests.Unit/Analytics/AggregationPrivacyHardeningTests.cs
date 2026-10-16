namespace Autheris.Tests.Unit.Analytics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Security;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// PLAN-DATA-112 WP-0 (standalone aggregation privacy hardening): SEC-112-01, -02, -03, -04, -07, F-1a, F-4 and F-7.
/// </summary>
public sealed class AggregationPrivacyHardeningTests
{
    private static readonly TableIdentifier OrdersTableId = new("sales", "public", "orders");

    private static TableMetadata CreateOrdersMetadata(int? minGroupSize = null)
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
                new TableColumn { TableId = tableId, ColumnName = "department", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "status", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "amount", DataType = "decimal" },
                new TableColumn { TableId = tableId, ColumnName = "denied_col", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "discount_secret", DataType = "decimal", IsSensitive = true }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["discount_secret"] = new MaskingRule { RuleType = "REDACT" }
            }
        };
    }

    private static TableAccessDecision CreateDecision() =>
        TableAccessDecision.Allowed(
            OrdersTableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["department"] = ColumnAccessLevel.Clear,
                ["status"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Clear,
                ["denied_col"] = ColumnAccessLevel.Deny,
                ["discount_secret"] = ColumnAccessLevel.Mask
            });

    private static ClaimsPrincipal CreatePrincipal(string sid = "S-1-5-21-ANALYST") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, sid),
            new Claim(ClaimTypes.Name, sid),
            new Claim(ClaimTypes.Role, "Analyst"),
            new Claim("tenant_id", "tenant-a")
        ], "TestAuth"));

    private sealed class RowExecutor(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IDataSourceExecutor
    {
        public int Calls { get; private set; }

        public DataSourceType SupportedType => DataSourceType.Sql;

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
            DataSourceExecutionContext context,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(rows);
        }
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

    private static (GovernedAggregationService Service, RowExecutor Executor, CapturingAuditLogRepository Audit) CreateService(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        AnalyticsOptions options,
        TableMetadata? metadata = null)
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>()).Returns(metadata ?? CreateOrdersMetadata());
        var executor = new RowExecutor(rows);
        var audit = new CapturingAuditLogRepository();
        var service = new GovernedAggregationService(
            metaRepo,
            audit,
            dataSourceExecutors: [executor],
            analyticsOptions: Options.Create(options),
            dpEngine: null);
        return (service, executor, audit);
    }

    private static List<IReadOnlyDictionary<string, object?>> DepartmentRows(params (string Department, int Count, decimal Amount)[] groups)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var (department, count, amount) in groups)
        {
            for (int i = 0; i < count; i++)
            {
                rows.Add(new Dictionary<string, object?> { ["department"] = department, ["status"] = "x", ["amount"] = amount + i });
            }
        }

        return rows;
    }

    private static AnalyticsOptions NoiseOptions(double epsilon = 1.0, double budget = 100.0, bool exposeCounts = true) => new()
    {
        Privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 2,
            ExposeSuppressedGroupsCount = exposeCounts,
            Noise = new AnalyticsNoiseOptions
            {
                Enabled = true,
                Epsilon = epsilon,
                DailyBudgetEpsilon = budget,
                MeasureBounds = new Dictionary<string, MeasureBoundsOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["amount"] = new MeasureBoundsOptions { Min = 0, Max = 1000 }
                }
            }
        }
    };

    // ---------------------------------------------------------------- W0-1 / SEC-112-02 / T-S-01

    [Theory]
    [InlineData(1, 5)]
    [InlineData(3, 5)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    public async Task ExecuteAggregationAsync_MinGroupSizeOverride_CanOnlyRaiseTheOperatorFloor(int overrideValue, int expected)
    {
        var (service, _, _) = CreateService(DepartmentRows(("A", 6, 1m)), new AnalyticsOptions
        {
            Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 5 }
        });

        var result = await service.ExecuteAggregationAsync(CreatePrincipal(), new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            MinGroupSizeOverride: overrideValue,
            PreEvaluatedDecision: CreateDecision()));

        result.MinimumGroupSize.ShouldBe(expected);
    }

    [Fact]
    public async Task ExecuteAggregationAsync_MinGroupSizeOverride_DoesNotLowerTableMinimumOrDatasetFloor()
    {
        var options = new AnalyticsOptions
        {
            Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 2 },
            TableMinimumGroupSizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["sales.public.orders"] = 7 }
        };
        var (service, _, _) = CreateService(DepartmentRows(("A", 8, 1m)), options);

        var result = await service.ExecuteAggregationAsync(CreatePrincipal(), new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            MinGroupSizeOverride: 1,
            PreEvaluatedDecision: CreateDecision()));

        result.MinimumGroupSize.ShouldBe(7);
    }

    [Fact]
    public async Task ExecuteAggregationAsync_StrategyOverrideMerge_DoesNotWeakenOperatorSuppress()
    {
        var options = new AnalyticsOptions
        {
            Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 5, SuppressionMode = SmallGroupStrategy.Suppress }
        };
        var (service, _, _) = CreateService(DepartmentRows(("Big", 6, 1m), ("S1", 3, 1m), ("S2", 3, 1m)), options);

        var result = await service.ExecuteAggregationAsync(CreatePrincipal(), new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            SmallGroupStrategyOverride: SmallGroupStrategy.MergeIntoOther,
            PreEvaluatedDecision: CreateDecision()));

        result.Metadata.Strategy.ShouldBe(SmallGroupStrategy.Suppress);
        result.MergedIntoOther.ShouldBeFalse();
        result.Suppressed.ShouldBeTrue();
    }

    // ---------------------------------------------------------------- W0-2 / SEC-112-01 / T-S-02, T-S-03

    [Theory]
    [InlineData("status = 'x' AND discount_secret > 1", "masked")]
    [InlineData("status = 'x' AND denied_col = 'y'", "unavailable")]
    [InlineData("status = 'x' AND no_such_col = 'y'", "unavailable")]
    public async Task ExecuteAggregationAsync_WhereWithPartialReferencedColumnSet_StillChecksTheWholePredicateText(string predicate, string expected)
    {
        var (service, executor, _) = CreateService(DepartmentRows(("A", 3, 1m)), new AnalyticsOptions());
        var where = new TableFilterClause(predicate, new Dictionary<string, object?>(), ["status"])
        {
            TrinoSqlFactory = _ => predicate
        };

        var query = new AggregationQuery(OrdersTableId, GroupBy: ["department"], Where: where, PreEvaluatedDecision: CreateDecision());

        if (expected == "masked")
        {
            await Should.ThrowAsync<MaskedAggregationException>(() => service.ExecuteAggregationAsync(CreatePrincipal(), query));
        }
        else
        {
            await Should.ThrowAsync<AggregationColumnUnavailableException>(() => service.ExecuteAggregationAsync(CreatePrincipal(), query));
        }

        executor.Calls.ShouldBe(0);
    }

    [Fact]
    public void CombineMetricFiltersWithWhere_UnionsRequestTextColumnsWithMetricFilterColumns()
    {
        var metadata = CreateOrdersMetadata();
        var metric = new DbtMetric("m", "simple", null, null, "{{ Dimension('orders__status') }} = 'open'", null, [], [], new Dictionary<string, string>());
        var request = TableFilterClause.FromTrinoText("discount_secret > 5", Array.Empty<string>());

        var combined = SemanticMetricQueryService.CombineMetricFiltersWithWhere([metric], request, metadata);

        combined.ShouldNotBeNull();
        combined!.ReferencedColumns.ShouldContain("discount_secret");
        combined.ReferencedColumns.ShouldContain("status");
    }

    [Fact]
    public void CombineMetricFiltersWithWhere_KeepsBothPredicatesAndRequestParameters_AndComposesFromTrinoText()
    {
        var metadata = CreateOrdersMetadata();
        var metric = new DbtMetric("m", "simple", null, null, "status = 'open'", null, [], [], new Dictionary<string, string>());
        var request = new TableFilterClause(
            "amount > @gq1",
            new Dictionary<string, object?> { ["gq1"] = 5 },
            ["amount"])
        {
            TrinoSqlFactory = resolve => $"\"{resolve("amount")}\" > @gq1"
        };

        var combined = SemanticMetricQueryService.CombineMetricFiltersWithWhere([metric], request, metadata);

        combined.ShouldNotBeNull();
        combined!.Parameters["gq1"].ShouldBe(5);
        combined.SqlPredicate.ShouldContain("amount > @gq1");
        combined.SqlPredicate.ShouldContain("status = 'open'");
        // The Trino text of the request clause is used, not its SqlPredicate.
        combined.GetTrinoPredicate().ShouldContain("\"amount\" > @gq1");
        combined.GetTrinoPredicate().ShouldContain("status = 'open'");
    }

    // ---------------------------------------------------------------- W0-3 / F-1a / T-S-04

    [Fact]
    public async Task ExecuteAggregationAsync_ExecutorReturnsExactlyTheRowCap_FailsClosedInsteadOfTruncating()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>(100_000);
        var row = new Dictionary<string, object?> { ["department"] = "A", ["amount"] = 1m };
        for (int i = 0; i < 100_000; i++)
        {
            rows.Add(row);
        }

        var (service, _, _) = CreateService(rows, new AnalyticsOptions());

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => service.ExecuteAggregationAsync(
            CreatePrincipal(),
            new AggregationQuery(OrdersTableId, GroupBy: ["department"], PreEvaluatedDecision: CreateDecision())));

        ex.ErrorCode.ShouldBe("AGGREGATION_ROW_CAP_EXCEEDED");
    }

    [Fact]
    public async Task ExecuteAggregationAsync_ExecutorReturnsBelowTheRowCap_Succeeds()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>(99_999);
        var row = new Dictionary<string, object?> { ["department"] = "A", ["amount"] = 1m };
        for (int i = 0; i < 99_999; i++)
        {
            rows.Add(row);
        }

        var (service, _, _) = CreateService(rows, new AnalyticsOptions());

        var result = await service.ExecuteAggregationAsync(
            CreatePrincipal(),
            new AggregationQuery(OrdersTableId, GroupBy: ["department"], PreEvaluatedDecision: CreateDecision()));

        result.Groups.Single().Count.ShouldBe(99_999);
    }

    // ---------------------------------------------------------------- W0-4 / F-4 / T-S-09

    [Theory]
    [InlineData("groupBy", "denied_col")]
    [InlineData("groupBy", "no_such_col")]
    [InlineData("aggregate", "denied_col")]
    [InlineData("aggregate", "no_such_col")]
    [InlineData("where", "denied_col")]
    [InlineData("where", "no_such_col")]
    [InlineData("orderBy", "denied_col")]
    [InlineData("orderBy", "no_such_col")]
    public async Task ExecuteAggregationAsync_DeniedAndUnknownColumns_ProduceOneIdenticalErrorWithoutTheName(string position, string column)
    {
        var (service, executor, audit) = CreateService(DepartmentRows(("A", 3, 1m)), new AnalyticsOptions());
        var decision = CreateDecision();

        var query = position switch
        {
            "groupBy" => new AggregationQuery(OrdersTableId, GroupBy: [column], PreEvaluatedDecision: decision),
            "aggregate" => new AggregationQuery(
                OrdersTableId,
                GroupBy: ["department"],
                Aggregates: [new AggregateSelection(AggregateFunction.Count, column)],
                PreEvaluatedDecision: decision),
            "where" => new AggregationQuery(
                OrdersTableId,
                GroupBy: ["department"],
                Where: TableFilterClause.FromTrinoText($"{column} = 'y'", [column]),
                PreEvaluatedDecision: decision),
            _ => new AggregationQuery(
                OrdersTableId,
                GroupBy: ["department"],
                OrderBy: [new TableOrderBy(column, false)],
                PreEvaluatedDecision: decision)
        };

        var ex = await Should.ThrowAsync<AggregationColumnUnavailableException>(() => service.ExecuteAggregationAsync(CreatePrincipal(), query));

        ex.ErrorCode.ShouldBe("AGGREGATION_COLUMN_UNAVAILABLE");
        ex.Message.ShouldBe("The requested column is not available for aggregation.");
        ex.Message.ShouldNotContain(column);
        executor.Calls.ShouldBe(0);
        // The audit reason is the same fixed text for unknown and denied columns and carries no column name.
        var denial = audit.Entries.Last(e => e.DetailsJson != null);
        using var details = System.Text.Json.JsonDocument.Parse(denial.DetailsJson!);
        var reasons = details.RootElement.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ToList();
        reasons.ShouldBe(["The requested column is not available for aggregation."]);
        reasons.ShouldAllBe(r => !r!.Contains(column));
    }

    // ---------------------------------------------------------------- W0-5 / SEC-112-04 / T-S-06

    [Fact]
    public async Task ExecuteAggregationAsync_SuppressedRowsCount_IsZeroWhenTheGroupsCountIsNotExposed()
    {
        var (service, _, audit) = CreateService(
            DepartmentRows(("Big", 6, 1m), ("Tiny", 1, 1m)),
            new AnalyticsOptions { Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 5, ExposeSuppressedGroupsCount = false } });

        var result = await service.ExecuteAggregationAsync(CreatePrincipal(), new AggregationQuery(
            OrdersTableId, GroupBy: ["department"], PreEvaluatedDecision: CreateDecision()));

        result.Suppressed.ShouldBeTrue();
        result.SuppressedGroupsCount.ShouldBe(0);
        result.SuppressedRowsCount.ShouldBe(0);
        // The true values stay available for the audit trail.
        audit.Entries.Last().DetailsJson.ShouldContain("\"suppressed_rows_count\":1");
    }

    [Fact]
    public async Task ExecuteAggregationAsync_SuppressedRowsCount_IsZeroWhenNoiseIsEnabled()
    {
        var (service, _, _) = CreateService(DepartmentRows(("Big", 6, 1m), ("Tiny", 1, 1m)), NoiseOptions());

        var result = await service.ExecuteAggregationAsync(CreatePrincipal(), new AggregationQuery(
            OrdersTableId, GroupBy: ["department"], PreEvaluatedDecision: CreateDecision()));

        result.NoiseApplied.ShouldBeTrue();
        result.SuppressedRowsCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAggregationAsync_SuppressedRowsCount_IsExposedWhenAllowedAndNoiseIsOff()
    {
        var (service, _, _) = CreateService(
            DepartmentRows(("Big", 6, 1m), ("Tiny", 2, 1m)),
            new AnalyticsOptions { Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 5 } });

        var result = await service.ExecuteAggregationAsync(CreatePrincipal(), new AggregationQuery(
            OrdersTableId, GroupBy: ["department"], PreEvaluatedDecision: CreateDecision()));

        result.SuppressedRowsCount.ShouldBe(2);
    }

    // ---------------------------------------------------------------- W0-6 / SEC-112-07 / T-S-05

    [Fact]
    public async Task ExecuteAggregationAsync_MergedOtherRow_HasNoMinMaxCountDistinctOrAvg_AndTotalsDoNotLeakThem()
    {
        var options = new AnalyticsOptions
        {
            Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 3, SuppressionMode = SmallGroupStrategy.MergeIntoOther }
        };

        // Big: amounts 100..103 (4 rows). Small groups of size 1 and 2 hold the global minimum 1 and maximum 9999.
        var rows = DepartmentRows(("Big", 4, 100m));
        rows.Add(new Dictionary<string, object?> { ["department"] = "S1", ["amount"] = 1m });
        rows.Add(new Dictionary<string, object?> { ["department"] = "S2", ["amount"] = 9999m });
        rows.Add(new Dictionary<string, object?> { ["department"] = "S2", ["amount"] = 5000m });

        var (service, _, _) = CreateService(rows, options);
        var result = await service.ExecuteAggregationAsync(CreatePrincipal(), new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Count, null, "cnt"),
                new AggregateSelection(AggregateFunction.Sum, "amount", "sum_amount"),
                new AggregateSelection(AggregateFunction.Avg, "amount", "avg_amount"),
                new AggregateSelection(AggregateFunction.Min, "amount", "min_amount"),
                new AggregateSelection(AggregateFunction.Max, "amount", "max_amount"),
                new AggregateSelection(AggregateFunction.CountDistinct, "amount", "distinct_amount")
            ],
            PreEvaluatedDecision: CreateDecision()));

        result.MergedIntoOther.ShouldBeTrue();
        var other = result.Groups.Single(g => g.IsMergedOther);
        other.Count.ShouldBe(3);
        other.Aggregates["cnt"].ShouldBe(3L);
        other.Aggregates["sum_amount"].ShouldBe(15000m);
        other.Aggregates["avg_amount"].ShouldBeNull();
        other.Aggregates["min_amount"].ShouldBeNull();
        other.Aggregates["max_amount"].ShouldBeNull();
        other.Aggregates["distinct_amount"].ShouldBeNull();
        other.Min.Values.ShouldAllBe(v => v == null);
        other.Max.Values.ShouldAllBe(v => v == null);
        other.Avg.Values.ShouldAllBe(v => v == null);

        result.Totals.ShouldNotBeNull();
        result.Totals!.Aggregates["min_amount"].ShouldBeNull();
        result.Totals.Aggregates["max_amount"].ShouldBeNull();
        result.Totals.Aggregates["distinct_amount"].ShouldBeNull();
        result.Totals.Min.Values.ShouldAllBe(v => v == null);
        result.Totals.Max.Values.ShouldAllBe(v => v == null);
        // Count and sum of the whole visible set stay available.
        result.Totals.Count.ShouldBe(7);
        result.Totals.Aggregates["sum_amount"].ShouldBe(15406m);
    }

    // ---------------------------------------------------------------- W0-7 / SEC-112-03 / T-S-07

    [Fact]
    public async Task ExecuteAggregationAsync_NoiseOn_RejectsDuplicateFunctionAndColumnPairs()
    {
        var (service, executor, _) = CreateService(DepartmentRows(("A", 6, 1m)), NoiseOptions());

        var ex = await Should.ThrowAsync<GatewayInvalidQueryException>(() => service.ExecuteAggregationAsync(CreatePrincipal(), new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Count, "amount", "c1"),
                new AggregateSelection(AggregateFunction.Count, "amount", "c2")
            ],
            PreEvaluatedDecision: CreateDecision())));

        ex.ErrorCode.ShouldBe("AGGREGATION_ARGUMENT_INVALID");
        executor.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(AggregateFunction.Min)]
    [InlineData(AggregateFunction.Max)]
    public async Task ExecuteAggregationAsync_NoiseOn_RejectsMinAndMax(AggregateFunction function)
    {
        var (service, executor, _) = CreateService(DepartmentRows(("A", 6, 1m)), NoiseOptions());

        var ex = await Should.ThrowAsync<GatewayInvalidQueryException>(() => service.ExecuteAggregationAsync(CreatePrincipal(), new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            Aggregates: [new AggregateSelection(function, "amount", "x")],
            PreEvaluatedDecision: CreateDecision())));

        ex.ErrorCode.ShouldBe("AGGREGATION_ARGUMENT_INVALID");
        executor.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAggregationAsync_NoiseOff_AllowsDuplicatePairsAndMinMax()
    {
        var (service, _, _) = CreateService(DepartmentRows(("A", 6, 1m)), new AnalyticsOptions());

        var result = await service.ExecuteAggregationAsync(CreatePrincipal(), new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Min, "amount", "m1"),
                new AggregateSelection(AggregateFunction.Min, "amount", "m2")
            ],
            PreEvaluatedDecision: CreateDecision()));

        result.Groups.Single().Aggregates["m1"].ShouldBe(1m);
    }

    [Fact]
    public async Task ExecuteAggregationAsync_NoiseOn_ChargesEpsilonPerNoisedAggregatePlusTheRowCount()
    {
        var (service, _, _) = CreateService(DepartmentRows(("A", 6, 1m), ("B", 6, 1m)), NoiseOptions(epsilon: 1.5, budget: 100.0));

        var result = await service.ExecuteAggregationAsync(CreatePrincipal("S-1-5-21-BUDGET"), new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Count, null, "cnt"),
                new AggregateSelection(AggregateFunction.Sum, "amount", "sum_amount"),
                new AggregateSelection(AggregateFunction.Avg, "amount", "avg_amount")
            ],
            PreEvaluatedDecision: CreateDecision()));

        // Two column aggregates (sum, avg) plus the row count: 3 x 1.5.
        result.Metadata.EpsilonConsumed.ShouldBe(4.5);
    }

    // ---------------------------------------------------------------- W0-8 / F-6 / T-S-11

    private static DbtMetric MetricWithFilter(string name) =>
        new(name, "simple", "desc", "Label", "status = 'secret_filter'", "{ \"measure\": \"m\" }", ["region"], ["m"],
            new Dictionary<string, string> { ["model"] = "internal_model" }, Unit: "EUR");

    private static ClaimsPrincipal PrincipalWithRole(string role, string tenant = "tenant-a") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-" + role),
            new Claim(ClaimTypes.Name, role),
            new Claim(ClaimTypes.Role, role),
            new Claim("tenant_id", tenant)
        ], "TestAuth"));

    [Fact]
    public void SemanticMetricCatalogProjection_NonAdmin_GetsOnlyDescriptiveFields()
    {
        var projected = Autheris.Api.Endpoints.SemanticMetricCatalogProjection.Project([MetricWithFilter("mrr")], PrincipalWithRole("Analyst"));

        var json = System.Text.Json.JsonSerializer.Serialize(projected);
        json.ShouldContain("mrr");
        json.ShouldContain("EUR");
        json.ShouldNotContain("secret_filter");
        json.ShouldNotContain("internal_model");
        json.ShouldNotContain("measure");
    }

    [Theory]
    [InlineData("DbtAdmin")]
    [InlineData("GovernanceAdmin")]
    public void SemanticMetricCatalogProjection_Administrators_GetTheFullDefinition(string role)
    {
        var projected = Autheris.Api.Endpoints.SemanticMetricCatalogProjection.Project([MetricWithFilter("mrr")], PrincipalWithRole(role));

        System.Text.Json.JsonSerializer.Serialize(projected).ShouldContain("secret_filter");
    }

    [Fact]
    public async Task InMemorySemanticCatalog_MultiTenancy_TenantWithoutPartitionDoesNotSeeTheLegacyPartition()
    {
        var catalog = new Autheris.Application.Dbt.Services.InMemorySemanticCatalog();
        await catalog.RegisterManifestAsync(new DbtSemanticManifest([], [MetricWithFilter("legacy_metric")], "1.0"));
        var tenantB = new TenantId("tenant-b");

        (await catalog.ListMetricsAsync(null, tenantB)).ShouldBeEmpty();
        (await catalog.GetMetricAsync("legacy_metric", tenantB)).ShouldBeNull();
        (await catalog.ListSemanticModelsAsync(tenantB)).ShouldBeEmpty();
        // The claim-less principal is the legacy tenant itself and keeps its partition.
        (await catalog.ListMetricsAsync(null, TenantId.LegacySingleTenant)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task InMemorySemanticCatalog_SingleTenancy_KeepsTheLegacyFallback()
    {
        var options = Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { Mode = "Single" } });
        var catalog = new Autheris.Application.Dbt.Services.InMemorySemanticCatalog(options);
        await catalog.RegisterManifestAsync(new DbtSemanticManifest([], [MetricWithFilter("legacy_metric")], "1.0"));

        (await catalog.GetMetricAsync("legacy_metric", new TenantId("tenant-b"))).ShouldNotBeNull();
    }

    [Fact]
    public async Task QueryMetricsAsync_ResolvesMetricsInThePrincipalsTenantOnly()
    {
        var catalog = new Autheris.Application.Dbt.Services.InMemorySemanticCatalog();
        await catalog.RegisterManifestAsync(new DbtSemanticManifest([], [MetricWithFilter("only_a")], "1.0"), new TenantId("tenant-a"));
        var service = new SemanticMetricQueryService(
            catalog,
            Substitute.For<IGovernedAggregationService>(),
            Substitute.For<ITableMetadataRepository>(),
            auditLogRepository: new CapturingAuditLogRepository());

        await Should.ThrowAsync<KeyNotFoundException>(() => service.QueryMetricsAsync(
            PrincipalWithRole("Analyst", tenant: "tenant-b"),
            new MetricQueryRequest(["only_a"])));

        // The owning tenant resolves the metric (it then fails later because the manifest has no semantic model, not with "metric not found").
        var ownerFailure = await Should.ThrowAsync<Exception>(() => service.QueryMetricsAsync(
            PrincipalWithRole("Analyst", tenant: "tenant-a"),
            new MetricQueryRequest(["only_a"])));
        ownerFailure.ShouldNotBeOfType<KeyNotFoundException>();
    }

    // ---------------------------------------------------------------- review fixes (W0-4 message, W0-6 noise, W0-2 union, W0-3 gateway branch)

    private static ClaimsPrincipal AnalystPrincipal => CreatePrincipal();

    private static async Task<(SemanticMetricQueryService Service, CapturingAuditLogRepository Audit)> CreateMetricServiceAsync(
        bool tablePresent,
        IUnifiedPolicyDecisionPoint? pdp = null)
    {
        var catalog = new Autheris.Application.Dbt.Services.InMemorySemanticCatalog();
        var model = new DbtSemanticModel(
            "orders_model",
            "orders",
            "sales.public.orders",
            [],
            [new DbtMeasure("total_revenue", "sum", "amount", null)],
            []);
        var metric = new DbtMetric("mrr", "simple", null, null, null, "{ \"measure\": \"total_revenue\" }", [], ["total_revenue"],
            new Dictionary<string, string> { ["model"] = "orders_model" });
        await catalog.RegisterManifestAsync(new DbtSemanticManifest([model], [metric], "1.0"), new TenantId("tenant-a"));

        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(tablePresent ? [CreateOrdersMetadata()] : []);
        if (tablePresent)
        {
            metaRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>()).Returns(CreateOrdersMetadata());
        }

        var audit = new CapturingAuditLogRepository();
        var service = new SemanticMetricQueryService(
            catalog,
            Substitute.For<IGovernedAggregationService>(),
            metaRepo,
            audit,
            pdp: pdp);
        return (service, audit);
    }

    [Fact]
    public async Task QueryMetricsAsync_DeniedTableAndMissingTable_AreIndistinguishableAndNameNothing()
    {
        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Denied(OrdersTableId, "secret-denial-reason"));

        var (denied, deniedAudit) = await CreateMetricServiceAsync(tablePresent: true, pdp);
        var (missing, missingAudit) = await CreateMetricServiceAsync(tablePresent: false);

        var deniedEx = await Should.ThrowAsync<GatewaySecurityException>(() => denied.QueryMetricsAsync(AnalystPrincipal, new MetricQueryRequest(["mrr"])));
        var missingEx = await Should.ThrowAsync<GatewaySecurityException>(() => missing.QueryMetricsAsync(AnalystPrincipal, new MetricQueryRequest(["mrr"])));

        deniedEx.GetType().ShouldBe(missingEx.GetType());
        deniedEx.ErrorCode.ShouldBe(missingEx.ErrorCode);
        deniedEx.ErrorCode.ShouldBe("FORBIDDEN");
        deniedEx.Message.ShouldBe(missingEx.Message);
        deniedEx.Message.ShouldBe("Access denied.");
        foreach (var text in new[] { deniedEx.Message, missingEx.Message })
        {
            text.ShouldNotContain("mrr");
            text.ShouldNotContain("orders");
            text.ShouldNotContain("secret-denial-reason");
        }

        // The true reasons stay in the audit trail.
        deniedAudit.Entries.ShouldContain(e => e.DetailsJson != null && e.DetailsJson.Contains("secret-denial-reason"));
        missingAudit.Entries.ShouldContain(e => e.DetailsJson != null && e.DetailsJson.Contains("not found"));
    }

    [Fact]
    public async Task ExecuteAggregationAsync_DeniedTable_MessageHasNeitherTableNameNorReasons_AuditKeepsThem()
    {
        var (service, _, audit) = CreateService(DepartmentRows(("A", 3, 1m)), new AnalyticsOptions());

        var ex = await Should.ThrowAsync<GatewayForbiddenException>(() => service.ExecuteAggregationAsync(
            CreatePrincipal(),
            new AggregationQuery(
                OrdersTableId,
                GroupBy: ["department"],
                PreEvaluatedDecision: TableAccessDecision.Denied(OrdersTableId, "secret-denial-reason"))));

        ex.Message.ShouldBe("Access denied.");
        ex.Message.ShouldNotContain("orders");
        ex.Message.ShouldNotContain("secret-denial-reason");
        audit.Entries.ShouldContain(e => e.DetailsJson != null && e.DetailsJson.Contains("secret-denial-reason"));
    }

    [Fact]
    public async Task ExecuteAggregationAsync_NoiseOnWithMergeIntoOther_KeepsNullForCountDistinctAndAvgOnTheMergedRow()
    {
        var options = NoiseOptions();
        options.Privacy.DefaultMinGroupSize = 3;
        options.Privacy.SuppressionMode = SmallGroupStrategy.MergeIntoOther;

        var rows = DepartmentRows(("Big", 4, 100m), ("S1", 1, 1m), ("S2", 2, 50m));
        var (service, _, _) = CreateService(rows, options);

        var result = await service.ExecuteAggregationAsync(CreatePrincipal(), new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Count, null, "cnt"),
                new AggregateSelection(AggregateFunction.Avg, "amount", "avg_amount"),
                new AggregateSelection(AggregateFunction.CountDistinct, "amount", "distinct_amount")
            ],
            PreEvaluatedDecision: CreateDecision()));

        result.NoiseApplied.ShouldBeTrue();
        result.MergedIntoOther.ShouldBeTrue();
        var other = result.Groups.Single(g => g.IsMergedOther);
        other.Aggregates["avg_amount"].ShouldBeNull();
        other.Aggregates["distinct_amount"].ShouldBeNull();
        result.Groups.Single(g => !g.IsMergedOther).Aggregates["avg_amount"].ShouldNotBeNull();
    }

    [Fact]
    public void CombineMetricFiltersWithWhere_RequestClauseWithoutTrinoBuilder_FailsClosedInsteadOfUsingSqlPredicate()
    {
        var metric = new DbtMetric("m", "simple", null, null, "status = 'open'", null, [], [], new Dictionary<string, string>());
        var request = new TableFilterClause("amount > 5", new Dictionary<string, object?>(), ["amount"]);

        var combined = SemanticMetricQueryService.CombineMetricFiltersWithWhere([metric], request, CreateOrdersMetadata());

        combined.ShouldNotBeNull();
        Should.Throw<NotSupportedException>(() => combined!.GetTrinoPredicate());
    }

    [Fact]
    public void CombineMetricFiltersWithWhere_BlankRequestClauseAndOneMetricFilter_KeepsTheMetricFilter()
    {
        var metric = new DbtMetric("m", "simple", null, null, "status = 'open'", null, [], [], new Dictionary<string, string>());
        var request = new TableFilterClause(" ", new Dictionary<string, object?>(), []);

        var combined = SemanticMetricQueryService.CombineMetricFiltersWithWhere([metric], request, CreateOrdersMetadata());

        combined.ShouldNotBeNull();
        combined!.SqlPredicate.ShouldContain("status = 'open'");
        combined.ReferencedColumns.ShouldContain("status");
        combined.GetTrinoPredicate().ShouldContain("status = 'open'");
    }

    [Fact]
    public async Task ExecuteAggregationAsync_ClauseWithSetAndParameters_StillChecksTheTextForDeniedColumns()
    {
        var (service, executor, _) = CreateService(DepartmentRows(("A", 3, 1m)), new AnalyticsOptions());
        var where = new TableFilterClause("status = @gq1 AND denied_col = 'y'", new Dictionary<string, object?> { ["gq1"] = "x" }, ["status"])
        {
            TrinoSqlFactory = _ => "status = @gq1 AND denied_col = 'y'"
        };

        await Should.ThrowAsync<AggregationColumnUnavailableException>(() => service.ExecuteAggregationAsync(
            CreatePrincipal(),
            new AggregationQuery(OrdersTableId, GroupBy: ["department"], Where: where, PreEvaluatedDecision: CreateDecision())));
        executor.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAggregationAsync_ParameterisedClause_PlaceholdersAreNotMistakenForColumns()
    {
        var (service, _, _) = CreateService(DepartmentRows(("A", 3, 1m)), new AnalyticsOptions());
        var where = new TableFilterClause("status = @gq1", new Dictionary<string, object?> { ["gq1"] = "x" }, ["status"])
        {
            TrinoSqlFactory = _ => "status = @gq1"
        };

        // The placeholder name gq1 is not a column; the query is accepted (no AGGREGATION_COLUMN_UNAVAILABLE).
        await Should.NotThrowAsync(() => service.ExecuteAggregationAsync(
            CreatePrincipal(),
            new AggregationQuery(OrdersTableId, GroupBy: ["department"], Where: where, PreEvaluatedDecision: CreateDecision())));
    }

    [Fact]
    public async Task ExecuteAggregationAsync_AtNameWithoutBoundParameter_IsStillCheckedAsAColumn()
    {
        var (service, executor, _) = CreateService(DepartmentRows(("A", 3, 1m)), new AnalyticsOptions());
        var where = new TableFilterClause("@denied_col = 'y'", new Dictionary<string, object?>(), [])
        {
            TrinoSqlFactory = _ => "@denied_col = 'y'"
        };

        // Only placeholders of bound parameters are removed before the text scan; an unbound @name keeps its identifier.
        await Should.ThrowAsync<AggregationColumnUnavailableException>(() => service.ExecuteAggregationAsync(
            CreatePrincipal(),
            new AggregationQuery(OrdersTableId, GroupBy: ["department"], Where: where, PreEvaluatedDecision: CreateDecision())));
        executor.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAggregationAsync_GatewayExecutionBranchReturningExactlyTheRowCap_FailsClosed()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>(100_000);
        var row = new Dictionary<string, object?> { ["department"] = "A", ["amount"] = 1m };
        for (int i = 0; i < 100_000; i++)
        {
            rows.Add(row);
        }

        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>()).Returns(CreateOrdersMetadata());
        var gateway = Substitute.For<IGatewayExecutionService>();
        gateway.ExecuteTableQueryAsync(Arg.Any<ClaimsPrincipal?>(), OrdersTableId, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((rows, CreateDecision()));
        var service = new GovernedAggregationService(
            metaRepo,
            new CapturingAuditLogRepository(),
            gatewayExecutionService: gateway,
            analyticsOptions: Options.Create(new AnalyticsOptions()),
            dpEngine: null);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => service.ExecuteAggregationAsync(
            CreatePrincipal(),
            new AggregationQuery(OrdersTableId, GroupBy: ["department"], PreEvaluatedDecision: CreateDecision())));

        ex.ErrorCode.ShouldBe("AGGREGATION_ROW_CAP_EXCEEDED");
    }

    [Fact]
    public async Task ExecuteAggregationAsync_PreAggregatedMergeIntoOther_NullsMinMaxCountDistinctAndAvg()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>()).Returns(CreateOrdersMetadata());
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        IReadOnlyDictionary<string, object?> SqlRow(string dept, long n, decimal min, decimal max) => new Dictionary<string, object?>
        {
            ["department"] = dept,
            [GovernedTableReadPlanner.GroupCountColumnAlias] = n,
            ["cnt"] = n,
            ["sum_amount"] = min + max,
            ["avg_amount"] = (min + max) / 2,
            ["min_amount"] = min,
            ["max_amount"] = max,
            ["distinct_amount"] = n
        };
        IReadOnlyList<IReadOnlyDictionary<string, object?>> sqlRows = [SqlRow("Big", 5, 100m, 200m), SqlRow("S1", 1, 1m, 1m), SqlRow("S2", 2, 5000m, 9999m)];
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("select", [], sqlRows, sqlRows.Count, 1));
        var service = new GovernedAggregationService(
            metaRepo,
            new CapturingAuditLogRepository(),
            sqlExecutionService: sql,
            analyticsOptions: Options.Create(new AnalyticsOptions
            {
                Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 3, SuppressionMode = SmallGroupStrategy.MergeIntoOther }
            }),
            dpEngine: null);

        var result = await service.ExecuteAggregationAsync(CreatePrincipal(), new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Count, null, "cnt"),
                new AggregateSelection(AggregateFunction.Sum, "amount", "sum_amount"),
                new AggregateSelection(AggregateFunction.Avg, "amount", "avg_amount"),
                new AggregateSelection(AggregateFunction.Min, "amount", "min_amount"),
                new AggregateSelection(AggregateFunction.Max, "amount", "max_amount"),
                new AggregateSelection(AggregateFunction.CountDistinct, "amount", "distinct_amount")
            ],
            PreEvaluatedDecision: CreateDecision()));

        result.MergedIntoOther.ShouldBeTrue();
        var other = result.Groups.Single(g => g.IsMergedOther);
        other.Aggregates["cnt"].ShouldBe(3L);
        other.Aggregates["sum_amount"].ShouldBe(15001m);
        other.Aggregates["avg_amount"].ShouldBeNull();
        other.Aggregates["min_amount"].ShouldBeNull();
        other.Aggregates["max_amount"].ShouldBeNull();
        other.Aggregates["distinct_amount"].ShouldBeNull();
        result.Totals!.Aggregates["min_amount"].ShouldBeNull();
        result.Totals.Aggregates["max_amount"].ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAggregationAsync_NoiseOn_OrderAndLimitFollowTheNoisedValues()
    {
        // Deterministic noise: the first sampled bucket (group A, true count 6) gets +100, the second (group B, true count 8) gets -5.
        var queue = new Queue<double>([100.0, -5.0]);
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>()).Returns(CreateOrdersMetadata());
        var service = new GovernedAggregationService(
            metaRepo,
            new CapturingAuditLogRepository(),
            dataSourceExecutors: [new RowExecutor(DepartmentRows(("A", 6, 1m), ("B", 8, 1m)))],
            analyticsOptions: Options.Create(NoiseOptions()),
            dpEngine: null)
        {
            NoiseSampler = _ => queue.Dequeue()
        };

        var result = await service.ExecuteAggregationAsync(CreatePrincipal("S-1-5-21-ORDER"), new AggregationQuery(
            OrdersTableId,
            GroupBy: ["department"],
            OrderBy: [new TableOrderBy("count", true)],
            Limit: 1,
            PreEvaluatedDecision: CreateDecision()));

        // Without noise B (8) would lead; with the noised counts A (106) leads and the limit keeps A only.
        result.Metadata.Truncated.ShouldBeTrue();
        result.Groups.Count.ShouldBe(1);
        result.Groups[0].GroupKey["department"].ShouldBe("A");
        result.Groups[0].Count.ShouldBe(106);
    }

    [Theory]
    [InlineData("AGGREGATION_COLUMN_UNAVAILABLE")]
    [InlineData("AGGREGATION_ROW_CAP_EXCEEDED")]
    public async Task McpQueryMetrics_CarriesTheAggregationErrorCodes(string code)
    {
        var service = Substitute.For<ISemanticMetricQueryService>();
        GatewaySecurityException thrown = code == "AGGREGATION_COLUMN_UNAVAILABLE"
            ? new AggregationColumnUnavailableException()
            : new AggregationRowCapExceededException();
        service.QueryMetricsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<MetricQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<MetricQueryResult>>(_ => throw thrown);

        var json = await Autheris.Application.Mcp.Services.McpSemanticTools.ExecuteQueryMetricsAsync(
            service, CreatePrincipal(), "{\"metrics\":[\"mrr\"]}");

        json.ShouldContain(code);
    }
}
