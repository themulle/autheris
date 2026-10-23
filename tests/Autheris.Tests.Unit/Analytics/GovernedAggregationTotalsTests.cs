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
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>MISC-08: totals merged from planner buckets (no raw rows) are never a wrong number: COUNT_DISTINCT is null, AVG is weighted by non-null counts.</summary>
public sealed class GovernedAggregationTotalsTests
{
    private static readonly TableIdentifier TableId = new("sales", "public", "orders");

    private static TableMetadata Metadata()
    {
        var id = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = TableId,
            Table = new Table
            {
                Id = id, SourceType = "PostgreSQL", SourceName = "default", SchemaName = "public", TableName = "orders",
                DataSourceType = DataSourceType.Sql, IsActive = true
            },
            Columns =
            [
                new TableColumn { TableId = id, ColumnName = "department", DataType = "varchar" },
                new TableColumn { TableId = id, ColumnName = "amount", DataType = "decimal" }
            ]
        };
    }

    private static TableAccessDecision Decision() =>
        TableAccessDecision.Allowed(
            TableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["department"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Clear
            }).AsPdpEvaluated();

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ANALYST"),
            new Claim(ClaimTypes.Name, "analyst"),
            new Claim(ClaimTypes.Role, "Analyst"),
            new Claim("tenant_id", "tenant-a")
        ], "TestAuth"));

    private sealed class NullAudit : IAuditLogRepository
    {
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);

        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
            string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private static IReadOnlyDictionary<string, object?> Row(string dept, long n, decimal avg, long? avgCount, long distinct) => new Dictionary<string, object?>
    {
        ["department"] = dept,
        [GovernedTableReadPlanner.GroupCountColumnAlias] = n,
        ["avg_amount"] = avg,
        ["dist_amount"] = distinct,
        ["cnt"] = n,
        [GovernedTableReadPlanner.AvgCountAliasPrefix + "avg_amount"] = avgCount
    };

    private static async Task<AggregationResult> RunAsync(params IReadOnlyDictionary<string, object?>[] sqlRows)
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(TableId, Arg.Any<CancellationToken>()).Returns(Metadata());
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("select", [], sqlRows, sqlRows.Length, 1));
        var service = new GovernedAggregationService(
            metaRepo,
            new NullAudit(),
            sqlExecutionService: sql,
            analyticsOptions: Options.Create(new AnalyticsOptions { Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 1 } }),
            dpEngine: null);
        var query = new AggregationQuery(
            TableId,
            GroupBy: ["department"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Count, null, "cnt"),
                new AggregateSelection(AggregateFunction.Avg, "amount", "avg_amount"),
                new AggregateSelection(AggregateFunction.CountDistinct, "amount", "dist_amount")
            ],
            PreEvaluatedDecision: Decision());
        return await service.ExecuteAggregationAsync(Principal(), query);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PlannerTotals_CountDistinct_IsNull()
    {
        var result = await RunAsync(Row("A", 5, 10m, 5, 3), Row("B", 5, 20m, 5, 3));

        result.Totals.ShouldNotBeNull();
        result.Totals!.Aggregates["dist_amount"].ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PlannerTotals_Avg_WeightedByNonNullCount()
    {
        // A: 1 row with a value (100); B: 10 rows of which only 1 has a value (10). True average of values = 55, row-weighted = 18.18.
        var result = await RunAsync(Row("A", 1, 100m, 1, 1), Row("B", 10, 10m, 1, 1));

        result.Totals.ShouldNotBeNull();
        Convert.ToDecimal(result.Totals!.Aggregates["avg_amount"]).ShouldBe(55m);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PlannerTotals_Avg_MissingCount_IsNull()
    {
        var result = await RunAsync(Row("A", 1, 100m, null, 1), Row("B", 10, 10m, null, 1));

        result.Totals.ShouldNotBeNull();
        result.Totals!.Aggregates["avg_amount"].ShouldBeNull();
    }
}
