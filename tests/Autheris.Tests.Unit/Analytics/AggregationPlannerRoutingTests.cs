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
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#112 WP-2 (A-5.1, A-5.2): SQL tables aggregate through the planner (no row cap), and a planner that cut groups reports it.</summary>
public sealed class AggregationPlannerRoutingTests
{
    private static readonly TableIdentifier TableId = new("sales", "public", "orders");

    private static TableMetadata Metadata(DataSourceType type = DataSourceType.Sql)
    {
        var id = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = TableId,
            Table = new Table
            {
                Id = id, SourceType = "PostgreSQL", SourceName = "default", SchemaName = "public", TableName = "orders",
                DataSourceType = type, IsActive = true
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
            });

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ANALYST"),
            new Claim(ClaimTypes.Name, "analyst"),
            new Claim(ClaimTypes.Role, "Analyst"),
            new Claim("tenant_id", "tenant-a")
        ], "TestAuth"));

    private static IReadOnlyDictionary<string, object?> SqlRow(string dept, long n) => new Dictionary<string, object?>
    {
        ["department"] = dept,
        [GovernedTableReadPlanner.GroupCountColumnAlias] = n,
        ["cnt"] = n
    };

    private sealed class CountingExecutor(int rowCount, DataSourceType type) : IDataSourceExecutor
    {
        public int Calls { get; private set; }

        public DataSourceType SupportedType => type;

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default)
        {
            Calls++;
            IReadOnlyDictionary<string, object?> row = new Dictionary<string, object?> { ["department"] = "A", ["amount"] = 1m };
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Enumerable.Repeat(row, rowCount).ToList());
        }
    }

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

    private static (GovernedAggregationService Service, IGovernedSqlExecutionService Sql, List<GovernedSqlQueryRequest> Requests) Create(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> sqlRows,
        CountingExecutor executor,
        int maxGroups = 1000,
        DataSourceType type = DataSourceType.Sql)
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(TableId, Arg.Any<CancellationToken>()).Returns(Metadata(type));
        var requests = new List<GovernedSqlQueryRequest>();
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                requests.Add(call.Arg<GovernedSqlQueryRequest>());
                return new GovernedSqlResult("select", [], sqlRows, sqlRows.Count, 1);
            });
        var service = new GovernedAggregationService(
            metaRepo,
            new NullAudit(),
            sqlExecutionService: sql,
            dataSourceExecutors: [executor],
            analyticsOptions: Options.Create(new AnalyticsOptions
            {
                Aggregation = new AggregationOptions { MaxGroups = maxGroups },
                Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 1 }
            }),
            dpEngine: null);
        return (service, sql, requests);
    }

    private static AggregationQuery Query() => new(
        TableId,
        GroupBy: ["department"],
        Aggregates: [new AggregateSelection(AggregateFunction.Count, null, "cnt")],
        PreEvaluatedDecision: Decision().AsPdpEvaluated());

    [Fact]
    public async Task Aggregation_SqlTable_Over100kRows_UsesPlanner_CountIs100001()
    {
        var executor = new CountingExecutor(100_000, DataSourceType.Sql);
        var (service, sql, _) = Create([SqlRow("A", 100_001)], executor);

        var result = await service.ExecuteAggregationAsync(Principal(), Query());

        result.Groups.Single().Aggregates["cnt"].ShouldBe(100_001L);
        executor.Calls.ShouldBe(0);
        await sql.Received(1).ExecuteQueryBufferedAsync(
            Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Aggregation_NonSqlSource_StillUsesTheExecutor_EvenWithASqlService()
    {
        var executor = new CountingExecutor(3, DataSourceType.HttpDeclarative);
        var (service, sql, _) = Create([SqlRow("A", 99)], executor, type: DataSourceType.HttpDeclarative);

        var result = await service.ExecuteAggregationAsync(Principal(), Query());

        executor.Calls.ShouldBe(1);
        result.Groups.Single().Aggregates["cnt"].ShouldBe(3L);
        await sql.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Aggregation_PlannerCutsGroups_SetsTruncated()
    {
        var executor = new CountingExecutor(0, DataSourceType.Sql);
        var (service, _, requests) = Create([SqlRow("A", 5), SqlRow("B", 4), SqlRow("C", 3)], executor, maxGroups: 2);

        var result = await service.ExecuteAggregationAsync(Principal(), Query());

        result.Metadata.Truncated.ShouldBeTrue();
        result.Groups.Count.ShouldBe(2);
        requests.Single().Sql.ShouldEndWith("LIMIT 3");
    }

    [Fact]
    public async Task Aggregation_PlannerReturnsExactlyTheLimit_IsNotTruncated()
    {
        var executor = new CountingExecutor(0, DataSourceType.Sql);
        var (service, _, requests) = Create([SqlRow("A", 5), SqlRow("B", 4)], executor, maxGroups: 2);

        var result = await service.ExecuteAggregationAsync(Principal(), Query());

        result.Metadata.Truncated.ShouldBeFalse();
        result.Groups.Count.ShouldBe(2);
        requests.Single().Sql.ShouldEndWith("LIMIT 3");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Aggregation_TypedWhere_SendsGatewayParameters_NotClientParameters()
    {
        var executor = new CountingExecutor(0, DataSourceType.Sql);
        var (service, _, requests) = Create([SqlRow("A", 5)], executor);
        var where = TreeFilterClauseBuilder.Build(
            new TreeComparison("department", TreeFilterOperator.Eq, "north"),
            _ => new ColumnRef("department", ColumnValueKind.String));

        await service.ExecuteAggregationAsync(Principal(), Query() with { Where = where });

        var request = requests.Single();
        request.Parameters.ShouldBeNull();
        request.PlannedTableRead.ShouldBeTrue();
        request.Sql.ShouldNotContain("north");
        request.GatewayParameters.ShouldNotBeNull();
        request.GatewayParameters.Keys.ShouldNotBeEmpty();
        request.GatewayParameters.Keys.ShouldAllBe(k => k.TrimStart('@').StartsWith("__gql_tf_", StringComparison.Ordinal));
        request.GatewayParameters.Values.ShouldContain("north");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Aggregation_WithoutWhere_SendsNoGatewayParameters()
    {
        var executor = new CountingExecutor(0, DataSourceType.Sql);
        var (service, _, requests) = Create([SqlRow("A", 5)], executor);

        await service.ExecuteAggregationAsync(Principal(), Query());

        requests.Single().GatewayParameters.ShouldBeNull();
        requests.Single().Parameters.ShouldBeNull();
    }
}
