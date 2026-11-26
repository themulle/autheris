namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class McpIsMaskedSignalTests
{
    private const string Clean = """{"data":{"items":[{"id":1,"city":"Berlin"}]}}""";

    private static McpSessionContext Session() =>
        new("sess-1181", "agent-principal", "tenant-alpha", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static AiDataGuardrailService Guardrail(Func<McpToolDefinition, string> execute, int? maxTokens = null) =>
        new(
            new McpToolRegistry(),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true, MaxTokensPerCall = maxTokens ?? new McpOptions().MaxTokensPerCall } }),
            NullLogger<AiDataGuardrailService>.Instance,
            Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            queryExecutor: new StubMcpQueryExecutor((tool, _, _) => execute(tool)),
            decisionPoint: Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AllowAll(),
            tableMetadataRepository: Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AnyTable());

    // ---- guardrail -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Guardrail_PolicyMasked_IsMaskedTrue()
    {
        var result = await Guardrail(_ => { McpResultMasking.Report(true); return Clean; })
            .ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_customers", "{}"), Session());

        result.IsSuccess.ShouldBeTrue();
        result.IsMasked.ShouldBeTrue();
    }

    [Fact]
    public async Task Guardrail_NoMasks_IsMaskedFalse()
    {
        var result = await Guardrail(_ => { McpResultMasking.Report(false); return Clean; })
            .ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_customers", "{}"), Session());

        result.IsSuccess.ShouldBeTrue();
        result.IsMasked.ShouldBeFalse();
    }

    [Fact]
    public async Task Guardrail_DataToolWithoutOutcome_IsMaskedTrue()
    {
        var result = await Guardrail(_ => Clean)
            .ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_customers", "{}"), Session());

        result.IsSuccess.ShouldBeTrue();
        result.IsMasked.ShouldBeTrue();
    }

    [Fact]
    public async Task Guardrail_MetadataToolWithoutOutcome_IsMaskedFalse()
    {
        var result = await Guardrail(_ => Clean)
            .ExecuteToolWithGuardrailAsync(new McpToolCallRequest("describe_api", "{}"), Session());

        result.IsSuccess.ShouldBeTrue();
        result.IsMasked.ShouldBeFalse();
    }

    [Fact]
    public async Task Guardrail_ScrubSignal_StillOrCombined()
    {
        var result = await Guardrail(_ => { McpResultMasking.Report(false); return """{"data":{"email":"erika.mustermann@acme-corp.com"}}"""; })
            .ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_customers", "{}"), Session());

        result.IsSuccess.ShouldBeTrue();
        result.IsMasked.ShouldBeTrue();
    }

    [Fact]
    public async Task Guardrail_ErrorResult_CarriesPolicySignal()
    {
        var result = await Guardrail(_ => { McpResultMasking.Report(true); return McpToolError.Serialize("EXECUTION_FAILED", "failed"); })
            .ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_customers", "{}"), Session());

        result.IsSuccess.ShouldBeFalse();
        result.IsMasked.ShouldBeTrue();
    }

    [Fact]
    public async Task Guardrail_ErrorResult_UnknownOutcome_IsFalse()
    {
        var result = await Guardrail(_ => McpToolError.Serialize("EXECUTION_FAILED", "failed"))
            .ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_customers", "{}"), Session());

        result.IsSuccess.ShouldBeFalse();
        result.IsMasked.ShouldBeFalse();
    }

    [Fact]
    public async Task Guardrail_BudgetTruncated_KeepsPolicySignal()
    {
        var big = JsonSerializer.Serialize(new { data = new { items = Enumerable.Range(0, 400).Select(i => new { id = i, city = "Berlin-" + i }) } });
        var result = await Guardrail(_ => { McpResultMasking.Report(true); return big; }, maxTokens: 30)
            .ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_customers", "{}"), Session());

        result.TruncatedDueToBudget.ShouldBeTrue();
        result.IsMasked.ShouldBeTrue();
    }

    [Fact]
    public async Task Guardrail_ExecutorThrows_ScopeDisposed()
    {
        using var outer = McpResultMasking.Begin();
        var guardrail = Guardrail(_ => { McpResultMasking.Report(true); throw new InvalidOperationException("boom"); });

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_customers", "{}"), Session()));

        outer.Outcome.ShouldBeNull();
        McpResultMasking.Report(false);
        outer.Outcome.ShouldBe(false);
    }

    [Fact]
    public async Task ResultJson_HasNoNewKeys_ForMaskedResult()
    {
        var masked = await RunQuerySqlAsync(SqlResult(true));
        var clear = await RunQuerySqlAsync(SqlResult(false));

        Keys(masked).ShouldBe(Keys(clear));
        masked.ToLowerInvariant().ShouldNotContain("masked");
    }

    private static string[] Keys(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
    }

    // ---- handler producers ---------------------------------------------------------------------------------------------------------

    private static GovernedSqlResult SqlResult(bool masked, int rows = 1) =>
        new("SELECT id, ssn FROM sales.dbo.customers", ["id", "ssn"],
            Enumerable.Range(0, rows).Select(_ => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = 1, ["ssn"] = "***" }).ToList(),
            rows, 1, MaskedColumns: [false, masked]);

    private static McpToolExecutionHandler Handler(
        GovernedSqlResult? sql = null,
        DatasetQueryEnvelope? dataset = null,
        ISemanticMetricQueryService? metrics = null) =>
        new(
            new StubGovernedSqlExecutionService { OnBuffered = (_, _, _, _) => Task.FromResult(sql ?? SqlResult(false)) },
            new FakeDataQueryService(dataset),
            new StubCatalogDiscoveryService(),
            new NotSupportedApiDispatcher(),
            new InMemoryLineageGraphStore(),
            new InMemoryTableMetadataRepository(),
            metricQueryService: metrics);

    private static async Task<string> RunQuerySqlAsync(GovernedSqlResult result)
    {
        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.QuerySql);
        return await Handler(sql: result).ExecuteToolAsync(tool, """{"query":"SELECT id, ssn FROM sales.dbo.customers"}""", Session());
    }

    [Fact]
    public async Task QuerySql_MaskedColumn_Reports()
    {
        using var scope = McpResultMasking.Begin();
        await RunQuerySqlAsync(SqlResult(true));
        scope.Outcome.ShouldBe(true);
    }

    [Fact]
    public async Task QuerySql_ZeroRows_ReportsFalse()
    {
        using var scope = McpResultMasking.Begin();
        await RunQuerySqlAsync(SqlResult(true, rows: 0));
        scope.Outcome.ShouldBe(false);
    }

    [Fact]
    public async Task QuerySql_UnmaskedColumns_ReportsFalse()
    {
        using var scope = McpResultMasking.Begin();
        await RunQuerySqlAsync(SqlResult(false));
        scope.Outcome.ShouldBe(false);
    }

    private static DatasetQueryEnvelope Envelope(bool masked, int rows) =>
        new("sales.dbo.customers", rows, 0, 50, false,
            [new DatasetColumnInfo("id", "int", false), new DatasetColumnInfo("ssn", "string", masked)],
            Enumerable.Range(0, rows).Select(_ => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = 1, ["ssn"] = "***" }).ToList());

    private static async Task<string> RunQueryDatasetAsync(DatasetQueryEnvelope envelope)
    {
        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.QueryDataset);
        return await Handler(dataset: envelope).ExecuteToolAsync(tool, """{"dataset":"sales.dbo.customers"}""", Session());
    }

    [Fact]
    public async Task QueryDataset_MaskedColumn_Reports()
    {
        using var scope = McpResultMasking.Begin();
        await RunQueryDatasetAsync(Envelope(masked: true, rows: 2));
        scope.Outcome.ShouldBe(true);
    }

    [Fact]
    public async Task QueryDataset_Unmasked_ReportsFalse()
    {
        using var scope = McpResultMasking.Begin();
        await RunQueryDatasetAsync(Envelope(masked: false, rows: 2));
        scope.Outcome.ShouldBe(false);
    }

    [Fact]
    public async Task QueryDataset_MaskedColumn_ZeroRows_ReportsFalse()
    {
        using var scope = McpResultMasking.Begin();
        await RunQueryDatasetAsync(Envelope(masked: true, rows: 0));
        scope.Outcome.ShouldBe(false);
    }

    private static async Task<string> RunQueryMetricsAsync(ISemanticMetricQueryService service)
    {
        var tool = McpSemanticTools.Definitions().First(t => t.Name == McpSemanticTools.QueryMetrics);
        return await Handler(metrics: service).ExecuteToolAsync(tool, """{"metrics":["revenue"]}""", Session());
    }

    [Fact]
    public async Task QueryMetrics_Success_ReportsFalse()
    {
        using var scope = McpResultMasking.Begin();
        await RunQueryMetricsAsync(new FakeMetricService(() => Metrics()));
        scope.Outcome.ShouldBe(false);
    }

    [Fact]
    public async Task QueryMetrics_MaskedMeasure_Throws_NoReport()
    {
        using var scope = McpResultMasking.Begin();
        await RunQueryMetricsAsync(new FakeMetricService(() => throw new MaskedAggregationException("ssn")));
        scope.Outcome.ShouldBeNull();
    }

    private static MetricQueryResult Metrics() =>
        new(["revenue"], "v1", [], new AggregationGroupRow(new Dictionary<string, object?>(), 1, new Dictionary<string, object?>()),
            new AggregationMetadata(5, SmallGroupStrategy.Suppress, false, 0, 0, false, false), new Dictionary<string, MetricDefinitionSummary>());

    // ---- invoke_api ----------------------------------------------------------------------------------------------------------------

    private static async Task<bool?> InvokeAsync(string endpoint, string method, GovernedSqlResult? sql = null, DatasetQueryEnvelope? dataset = null)
    {
        var dispatcher = new ApiDispatcherService(
            new FakeDataQueryService(dataset),
            catalogDiscoveryService: null,
            sqlExecutionService: new StubGovernedSqlExecutionService { OnBuffered = (_, _, _, _) => Task.FromResult(sql ?? SqlResult(false)) });
        using var scope = McpResultMasking.Begin();
        await dispatcher.InvokeApiAsync(endpoint, method, new Dictionary<string, object?> { ["sql"] = "select 1" });
        return scope.Outcome;
    }

    [Fact]
    public async Task Statement_MaskedResult_ReportsPolicyMasking() =>
        (await InvokeAsync("/api/v1/statement", "POST", sql: SqlResult(true))).ShouldBe(true);

    [Fact]
    public async Task Statement_UnmaskedResult_ReportsFalse() =>
        (await InvokeAsync("/api/v1/statement", "POST", sql: SqlResult(false))).ShouldBe(false);

    [Fact]
    public async Task Data_MaskedColumn_ReportsPolicyMasking() =>
        (await InvokeAsync("/api/v1/data/sales/dbo/customers", "GET", dataset: Envelope(masked: true, rows: 1))).ShouldBe(true);

    [Fact]
    public async Task AccessBranch_ReportsFalse() =>
        (await InvokeAsync("/api/v1/governance/me/access", "GET")).ShouldBe(false);

    // ---- fakes ---------------------------------------------------------------------------------------------------------------------

    private sealed class FakeDataQueryService(DatasetQueryEnvelope? envelope) : IGovernedDataQueryService
    {
        public Task<DatasetQueryEnvelope> ExecuteQueryAsync(DatasetQueryRequest request, RequestContext context, CancellationToken ct = default) =>
            Task.FromResult(envelope ?? throw new NotSupportedException());
    }

    private sealed class FakeMetricService(Func<MetricQueryResult> result) : ISemanticMetricQueryService
    {
        public Task<MetricQueryResult> QueryMetricsAsync(ClaimsPrincipal principal, MetricQueryRequest request, CancellationToken ct = default) =>
            Task.FromResult(result());
    }

    private sealed class NotSupportedApiDispatcher : IApiDispatcherService
    {
        public Task<string> DescribeApiAsync(string endpoint, string? method = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> InvokeApiAsync(string endpoint, string method, IReadOnlyDictionary<string, object?>? parameters = null, object? body = null, RequestContext? context = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetOpenApiJsonAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEndpointsDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetMcpToolsDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetQueryGuideDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
