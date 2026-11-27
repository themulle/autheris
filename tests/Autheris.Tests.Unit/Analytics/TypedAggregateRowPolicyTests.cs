namespace Autheris.Tests.Unit.Analytics;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Analytics;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Mcp;
using Autheris.Tests.Unit.Sql;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Issues #1035 and #1038: Non-raising typed SUM/AVG aggregates compile under row predicates across both
/// fenced and unfenced dialects, deserialize accurately, and guard refusals map cleanly with code and hint.
/// </summary>
public sealed class TypedAggregateRowPolicyTests
{
    private static readonly TableIdentifier SalesTableId = new("sales", "dbo", "umsaetze");

    private static TableMetadata CreateMetadata(string sourceType, string columnType = "int")
    {
        var tableId = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = SalesTableId,
            MinGroupSize = 1,
            Table = new Table
            {
                Id = tableId,
                SourceType = sourceType,
                SourceName = "default",
                SchemaName = "dbo",
                TableName = "umsaetze",
                DataSourceType = DataSourceType.Sql,
                IsActive = true,
                MinGroupSize = 1
            },
            Columns =
            [
                new TableColumn { TableId = tableId, ColumnName = "id", DataType = "bigint" },
                new TableColumn { TableId = tableId, ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "region", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "gruppe", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "wert", DataType = columnType }
            ]
        };
    }

    private static PolicyPredicate RegionNordPredicate() => PolicyPredicate.Create(
        new BinaryExpression(
            new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("region", true) })),
            BinaryOperator.Equal,
            new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_region"] = new("Nord", SqlParameterType.String) });

    private static CompiledSql CompilePlannedSql(string plannedSql, TargetSqlDialect dialect, string columnType)
    {
        var env = new TestCompileEnv(dialect, "dbo")
            .Table("umsaetze", "tenant_id", "id:bigint", "tenant_id:varchar", "region:varchar", "gruppe:varchar", $"wert:{columnType}")
            .Policy("umsaetze", RegionNordPredicate());
        return env.Compile(plannedSql);
    }

    public static TheoryData<string, TargetSqlDialect, string> DialectAndColumnTypeMatrix()
    {
        var data = new TheoryData<string, TargetSqlDialect, string>();
        var dialects = new (string Source, TargetSqlDialect Target)[]
        {
            ("PostgreSQL", TargetSqlDialect.PostgreSql),
            ("SqlServer", TargetSqlDialect.SqlServer),
            ("Oracle", TargetSqlDialect.Oracle),
            ("Databricks", TargetSqlDialect.Databricks)
        };
        var colTypes = new[] { "int", "bigint", "decimal(18,2)", "double" };

        foreach (var (source, target) in dialects)
        {
            foreach (var colType in colTypes)
            {
                data.Add(source, target, colType);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DialectAndColumnTypeMatrix))]
    public void Planner_SumAndAvg_WithoutGroupBy_CompilesUnderRowPredicate_OnFencedAndUnfencedDialects(
        string sourceType,
        TargetSqlDialect dialect,
        string columnType)
    {
        var metadata = CreateMetadata(sourceType, columnType);
        var clearCols = metadata.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase);
        var decision = TableAccessDecision.Allowed(SalesTableId, clearCols, rowFilterSql: "region = 'Nord'");

        var intent = new TableAggregateIntent(
            SalesTableId,
            GroupBy: [],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Sum, "wert", "s"),
                new AggregateSelection(AggregateFunction.Avg, "wert", "a")
            ],
            Filter: null,
            OrderBy: null,
            Limit: 1);

        var planned = new GovernedTableReadPlanner().PlanAggregate(intent, metadata, decision);

        if (dialect is TargetSqlDialect.SqlServer or TargetSqlDialect.Databricks or TargetSqlDialect.DuckDb)
        {
            planned.Sql.ShouldContain("TRY_CAST(");
        }

        var compiled = CompilePlannedSql(planned.Sql, dialect, columnType);
        compiled.Sql.ShouldNotBeNullOrWhiteSpace();
        compiled.Sql.ShouldContain("region", Case.Insensitive);
    }

    [Theory]
    [MemberData(nameof(DialectAndColumnTypeMatrix))]
    public void Planner_SumAndAvg_WithGroupBy_CompilesUnderRowPredicate_OnFencedAndUnfencedDialects(
        string sourceType,
        TargetSqlDialect dialect,
        string columnType)
    {
        var metadata = CreateMetadata(sourceType, columnType);
        var clearCols = metadata.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase);
        var decision = TableAccessDecision.Allowed(SalesTableId, clearCols, rowFilterSql: "region = 'Nord'");

        var intent = new TableAggregateIntent(
            SalesTableId,
            GroupBy: ["gruppe"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Count, null, "n"),
                new AggregateSelection(AggregateFunction.Sum, "wert", "s"),
                new AggregateSelection(AggregateFunction.Avg, "wert", "a")
            ],
            Filter: null,
            OrderBy: null,
            Limit: 10);

        var planned = new GovernedTableReadPlanner().PlanAggregate(intent, metadata, decision);
        var compiled = CompilePlannedSql(planned.Sql, dialect, columnType);

        compiled.Sql.ShouldNotBeNullOrWhiteSpace();
        compiled.Sql.ShouldContain("gruppe", Case.Insensitive);
        compiled.Sql.ShouldContain("region", Case.Insensitive);
    }

    [Fact]
    public void PlannedNonRaisingSumAndAvg_AlsoCompilesUnderRowPredicate_ForDuckDb()
    {
        var metadata = CreateMetadata("Databricks", "int");
        var clearCols = metadata.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase);
        var decision = TableAccessDecision.Allowed(SalesTableId, clearCols, rowFilterSql: "region = 'Nord'");

        var intent = new TableAggregateIntent(
            SalesTableId,
            GroupBy: ["gruppe"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Sum, "wert", "s"),
                new AggregateSelection(AggregateFunction.Avg, "wert", "a")
            ],
            Filter: null,
            OrderBy: null,
            Limit: 10);

        var planned = new GovernedTableReadPlanner().PlanAggregate(intent, metadata, decision);
        var compiled = CompilePlannedSql(planned.Sql, TargetSqlDialect.DuckDb, "int");

        compiled.Sql.ShouldNotBeNullOrWhiteSpace();
        compiled.Sql.ShouldContain("TRY_CAST", Case.Insensitive);
        compiled.Sql.ShouldContain("region", Case.Insensitive);
    }

    [Fact]
    public async Task GovernedAggregationService_HandlesBigIntegerAndDecimalAndDoubleResults_Accurately()
    {
        var metadata = CreateMetadata("Databricks", "int");
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(SalesTableId, Arg.Any<CancellationToken>()).Returns(metadata);

        var clearCols = metadata.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase);
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var sqlRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["gruppe"] = "A",
                [GovernedTableReadPlanner.GroupCountColumnAlias] = 3L,
                ["n"] = 3L,
                ["s"] = new BigInteger(600),
                ["a"] = 200.5d
            }
        };
        var capturedRequests = new List<GovernedSqlQueryRequest>();
        var sqlSvc = Substitute.For<IGovernedSqlExecutionService>();
        sqlSvc.ExecuteQueryBufferedAsync(
                Arg.Any<GovernedSqlQueryRequest>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TenantId>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequests.Add(call.Arg<GovernedSqlQueryRequest>());
                return new GovernedSqlResult("select", [], sqlRows, sqlRows.Count, 1);
            });

        var service = new GovernedAggregationService(
            metadataRepository: metaRepo,
            auditLogRepository: auditRepo,
            pdp: Autheris.Tests.Unit.TestSupport.AccessDecisionPointStubs.Returning(_ =>
                TableAccessDecision.Allowed(SalesTableId, clearCols, rowFilterSql: "region = 'Nord'")),
            sqlExecutionService: sqlSvc,
            dataSourceExecutors: [],
            analyticsOptions: Options.Create(new AnalyticsOptions
            {
                Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 1 }
            }));

        var principal = Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"),
            new Claim(ClaimTypes.Name, "user"),
            new Claim(ClaimTypes.Role, "Analyst"),
            new Claim("tenant_id", "t1")
        ], "TestAuth")));

        var result = await service.ExecuteAggregationAsync(principal, new AggregationQuery(
            Table: SalesTableId,
            GroupBy: ["gruppe"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Count, null, "n"),
                new AggregateSelection(AggregateFunction.Sum, "wert", "s"),
                new AggregateSelection(AggregateFunction.Avg, "wert", "a")
            ]));

        capturedRequests.Count.ShouldBe(1);
        capturedRequests[0].Sql.ShouldContain("TRY_CAST(");

        result.Groups.Count.ShouldBe(1);
        DecimalConversions.ToDecimal(result.Groups[0].Sum["wert"]).ShouldBe(600m);
        DecimalConversions.ToDecimal(result.Groups[0].Avg["wert"]).ShouldBe(200.5m);
        result.Totals.ShouldNotBeNull();
        result.Totals!.Count.ShouldBe(3L);
        result.Totals!.Sum["wert"].ShouldBe(600m);

        var gruppeField = new CatalogColumnField("gruppe", "gruppe", CatalogFieldType.String, "varchar", true);
        var wertField = new CatalogColumnField("wert", "wert", CatalogFieldType.Int, "int", true);
        var resolvedAggs = new[]
        {
            new ResolvedAggregate("n", AggregateFunction.Count, null, AggregateValueKind.Int),
            new ResolvedAggregate("s", AggregateFunction.Sum, wertField, AggregateValueKind.Decimal),
            new ResolvedAggregate("a", AggregateFunction.Avg, wertField, AggregateValueKind.Float)
        };
        var dto = AggregateResultMapper.Map(result, [gruppeField], resolvedAggs);
        dto.Groups.Count.ShouldBe(1);
        dto.Groups[0].Aggregates.Single(v => v.Alias == "n").IntValue.ShouldBe(3L);
        dto.Groups[0].Aggregates.Single(v => v.Alias == "s").DecimalValue.ShouldBe(600m);
        dto.Groups[0].Aggregates.Single(v => v.Alias == "a").FloatValue.ShouldBe(200.5d);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RowPolicyExpressionRejectedException_MapsToNamedCodeAndHint_NeverInternalServerError_AcrossAllSurfaces()
    {
        var ex = new RowPolicyExpressionRejectedException();

        // 1. GraphQL AggregateFailureMapper
        var gqlMapped = AggregateFailureMapper.ForAggregate(ex);
        gqlMapped.ShouldNotBeNull();
        gqlMapped!.Errors[0].Code.ShouldBe("ROW_POLICY_EXPRESSION_NOT_ALLOWED");
        gqlMapped.Errors[0].Code.ShouldNotBe("INTERNAL_SERVER_ERROR");
        var mappedHint = gqlMapped.Errors[0].Extensions!["hint"]?.ToString();
        mappedHint.ShouldNotBeNull();
        mappedHint!.ShouldContain("TRY_CAST");
        mappedHint.ShouldContain("NULLIF");

        // 2. GraphQL ErrorSanitizingFilter (both mapped and direct exception in Production & Development)
        foreach (var envName in new[] { "Production", "Development" })
        {
            var filter = new ErrorSanitizingFilter(new TestHostEnvironment(envName), NullLogger<ErrorSanitizingFilter>.Instance);

            var sanitizedMapped = filter.OnError(gqlMapped.Errors[0]);
            sanitizedMapped.Code.ShouldBe("ROW_POLICY_EXPRESSION_NOT_ALLOWED");
            sanitizedMapped.Code.ShouldNotBe("INTERNAL_SERVER_ERROR");
            sanitizedMapped.Extensions!["hint"]!.ToString()!.ShouldContain("TRY_CAST");

            var rawGqlErr = ErrorBuilder.New().SetMessage("internal").SetException(ex).Build();
            var sanitizedDirect = filter.OnError(rawGqlErr);
            sanitizedDirect.Code.ShouldBe("ROW_POLICY_EXPRESSION_NOT_ALLOWED");
            sanitizedDirect.Code.ShouldNotBe("INTERNAL_SERVER_ERROR");
            sanitizedDirect.Extensions!["hint"]!.ToString()!.ShouldContain("NULLIF");
        }

        // 3. SqlCompilerErrorMapper
        SqlCompilerErrorMapper.TryMap(ex, out var mapping).ShouldBeTrue();
        mapping.Status.ShouldBe(422);
        mapping.Code.ShouldBe("ROW_POLICY_EXPRESSION_NOT_ALLOWED");
        mapping.Hint.ShouldNotBeNull();
        mapping.Hint!.ShouldContain("TRY_CAST");
        mapping.Hint.ShouldContain("NULLIF");

        // 4. REST Analytics Endpoint
        var aggSvc = Substitute.For<IGovernedAggregationService>();
        aggSvc.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw ex);
        var aggCtx = NewHttpContext();
        var aggResult = await AnalyticsEndpoints.HandleAggregateAsync(
            new AnalyticsEndpoints.AggregateRequestDto("sales.dbo.umsaetze", Aggregates: [new AnalyticsEndpoints.AggregateSelectionDto("sum", "wert", "s")]),
            aggCtx,
            aggSvc,
            CancellationToken.None);
        await aggResult.ExecuteAsync(aggCtx);
        aggCtx.Response.StatusCode.ShouldBe(422);
        var aggJson = JsonDocument.Parse(ReadBody(aggCtx)).RootElement;
        aggJson.GetProperty("code").GetString().ShouldBe("ROW_POLICY_EXPRESSION_NOT_ALLOWED");
        aggJson.GetProperty("hint").GetString()!.ShouldContain("TRY_CAST");

        // 5. WebSQL Endpoint + Global Exception Handler
        var webSqlCtx = NewHttpContext();
        await WebSqlEndpoints.WriteWebSqlErrorAsync(webSqlCtx, ex, NullLogger.Instance, CancellationToken.None);
        webSqlCtx.Response.StatusCode.ShouldBe(422);
        var webSqlJson = JsonDocument.Parse(ReadBody(webSqlCtx)).RootElement;
        webSqlJson.GetProperty("code").GetString().ShouldBe("ROW_POLICY_EXPRESSION_NOT_ALLOWED");
        webSqlJson.GetProperty("hint").GetString()!.ShouldContain("NULLIF");

        var handlerCtx = NewHttpContext();
        var handled = await new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance)
            .TryHandleAsync(handlerCtx, ex, CancellationToken.None);
        handled.ShouldBeTrue();
        handlerCtx.Response.StatusCode.ShouldBe(422);
        JsonDocument.Parse(ReadBody(handlerCtx)).RootElement.GetProperty("code").GetString()
            .ShouldBe("ROW_POLICY_EXPRESSION_NOT_ALLOWED");

        // 6. MCP Executor
        var mcpHandler = Substitute.For<IMcpToolExecutionHandler>();
        mcpHandler.CanHandle(Arg.Any<string>()).Returns(true);
        mcpHandler.ExecuteToolAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw ex);
        var executor = new GatewayMcpQueryExecutor(
            Substitute.For<IRequestExecutorProvider>(),
            Substitute.For<IGatewayExecutionService>(),
            NullLogger<GatewayMcpQueryExecutor>.Instance,
            McpRowCapTests.Options(100),
            toolExecutionHandler: mcpHandler,
            persistedToolValidator: new PersistedToolValidator());
        var mcpJson = await executor.ExecuteOperationAsync(
            McpDatasetTools.Definitions().First(t => t.Name == "query_sql"),
            """{"sql":"SELECT 1"}""",
            new McpSessionContext("s1", "spn-1", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Roles: ["Reader"]));
        var mcpErr = JsonDocument.Parse(mcpJson).RootElement.GetProperty("error");
        mcpErr.GetProperty("code").GetString().ShouldBe("ROW_POLICY_EXPRESSION_NOT_ALLOWED");
        mcpErr.GetProperty("hint").GetString()!.ShouldContain("TRY_CAST");
    }

    private static DefaultHttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "TestAuth")),
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IHostEnvironment>(new TestHostEnvironment("Production"))
                .BuildServiceProvider(),
            TraceIdentifier = "trace-1038"
        };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body).ReadToEnd();
    }
}
