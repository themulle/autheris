namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Threading;
using System.Threading.Tasks;
using Antlr4.Runtime;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestDoubles;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

public sealed class McpDataTargetResolverAsyncTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task TryResolveSqlTablesAsync_WithScalarAndListPlaceholders_ResolvesSameTablesAsLiterals()
    {
        var engine = new RecordingAsyncSqlEngine();

        var (literal, literalError) = await McpDataTargetResolver.TryResolveSqlTablesAsync(
            "SELECT o.id FROM sales.public.orders o WHERE o.name = 'x' AND o.id IN (1, 2)", engine, "default");
        var (placeholders, placeholderError) = await McpDataTargetResolver.TryResolveSqlTablesAsync(
            "SELECT o.id FROM sales.public.orders o WHERE o.name = @n AND o.id IN (@ids)", engine, "default");

        literalError.ShouldBeNull();
        placeholderError.ShouldBeNull();
        placeholders.ShouldBe(literal);
        placeholders.ShouldBe([new TableIdentifier("sales", "public", "orders")]);
    }

    private sealed class RecordingAsyncSqlEngine : ISqlEngine, IAsyncSqlEngine
    {
        public int SyncAnalyzeCalls { get; private set; }
        public int AsyncAnalyzeCalls { get; private set; }

        public CompiledSql Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken cancellationToken) =>
            FastSqlEngine.Default.Compile(sql, request, cancellationToken);

        public (SqlBaseParser.SingleStatementContext Tree, CommonTokenStream Tokens) Parse(
            ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default) =>
            FastSqlEngine.Default.Parse(sql, tokenOptions, cancellationToken);

        public (SqlBaseParser.StandaloneExpressionContext Tree, CommonTokenStream Tokens) ParseExpression(
            ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default) =>
            FastSqlEngine.Default.ParseExpression(sql, tokenOptions, cancellationToken);

        public SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql)
        {
            SyncAnalyzeCalls++;
            throw new InvalidOperationException("Synchronous Analyze must not be called on async target resolution paths.");
        }

        public SqlQueryMetadata Analyze(
            ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions, CancellationToken cancellationToken = default)
        {
            SyncAnalyzeCalls++;
            throw new InvalidOperationException("Synchronous Analyze must not be called on async target resolution paths.");
        }

        public Task<(SqlBaseParser.SingleStatementContext Tree, CommonTokenStream Tokens)> ParseAsync(
            ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default) =>
            FastSqlEngine.Default.ParseAsync(sql, tokenOptions, cancellationToken);

        public Task<(SqlBaseParser.StandaloneExpressionContext Tree, CommonTokenStream Tokens)> ParseExpressionAsync(
            ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default) =>
            FastSqlEngine.Default.ParseExpressionAsync(sql, tokenOptions, cancellationToken);

        public async Task<SqlQueryMetadata> AnalyzeAsync(
            ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default)
        {
            AsyncAnalyzeCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            return await FastSqlEngine.Default.AnalyzeAsync(sql, tokenOptions, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class StubQueryExecutor : IMcpQueryExecutor
    {
        public int ExecutionCount { get; private set; }

        public Task<string> ExecuteOperationAsync(
            McpToolDefinition tool, string argumentsJson, McpSessionContext sessionContext, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return Task.FromResult("""{"rows":[{"id":1}]}""");
        }
    }

    [Fact]
    public async Task ParseTablesFromToolAsync_DispatchesSqlAndInvokeApiAsynchronously()
    {
        var engine = new RecordingAsyncSqlEngine();
        var registry = new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoDataSwitch(false));
        var querySqlTool = registry.FindTool(McpDatasetTools.QuerySql)!;
        var invokeApiTool = registry.FindTool(McpDatasetTools.InvokeApi)!;
        var listTool = registry.FindTool("list_datasets")!;

        var sqlTables = await AiDataGuardrailService.ParseTablesFromToolAsync(
            querySqlTool,
            """{"query":"SELECT id FROM sales.public.orders"}""",
            engine,
            "default");
        var apiTables = await AiDataGuardrailService.ParseTablesFromToolAsync(
            invokeApiTool,
            """{"endpoint":"/api/v1/statement","parameters":{"query":"SELECT id FROM hr.public.employees"}}""",
            engine,
            "default");
        var listTables = await AiDataGuardrailService.ParseTablesFromToolAsync(listTool, "{}", engine, "default");

        sqlTables.ShouldBe([new TableIdentifier("sales", "public", "orders")]);
        apiTables.ShouldBe([new TableIdentifier("hr", "public", "employees")]);
        listTables.ShouldBe([new TableIdentifier("governance", "catalog", "datasets")]);
        engine.AsyncAnalyzeCalls.ShouldBe(2);
        engine.SyncAnalyzeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task TryResolveSqlTablesAsync_UsesAsyncSqlEngineAndResolvesTables()
    {
        var engine = new RecordingAsyncSqlEngine();

        var (tables, syntaxError) = await McpDataTargetResolver.TryResolveSqlTablesAsync(
            "SELECT o.id FROM sales.public.orders o JOIN hr.public.employees e ON o.emp_id = e.id",
            engine,
            "default");

        syntaxError.ShouldBeNull();
        tables.ShouldNotBeNull();
        tables.ShouldBe([
            new TableIdentifier("sales", "public", "orders"),
            new TableIdentifier("hr", "public", "employees")
        ]);
        engine.AsyncAnalyzeCalls.ShouldBe(1);
        engine.SyncAnalyzeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task TryResolveSqlTablesAsync_NoTableSelect_ReturnsSqlNoTable()
    {
        var engine = new RecordingAsyncSqlEngine();

        var (tables, syntaxError) = await McpDataTargetResolver.TryResolveSqlTablesAsync("SELECT 1", engine, "default");

        syntaxError.ShouldBeNull();
        tables.ShouldBe([McpDataTargetResolver.SqlNoTable]);
        engine.AsyncAnalyzeCalls.ShouldBe(1);
        engine.SyncAnalyzeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task TryResolveSqlTablesAsync_KeywordlessSelfReference_ReturnsConstructNotSupported()
    {
        var engine = new RecordingAsyncSqlEngine();

        var (tables, syntaxError) = await McpDataTargetResolver.TryResolveSqlTablesAsync(
            "WITH orders AS (SELECT id FROM orders) SELECT id FROM orders",
            engine,
            "default");

        tables.ShouldBeNull();
        syntaxError.ShouldBe(McpDataTargetResolver.ConstructNotSupportedMarker);
        engine.AsyncAnalyzeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task TryResolveSqlTablesAsync_InvalidSql_ReturnsSyntaxError()
    {
        var engine = new RecordingAsyncSqlEngine();

        var (tables, syntaxError) = await McpDataTargetResolver.TryResolveSqlTablesAsync(
            "SELECT FROM WHERE",
            engine,
            "default");

        tables.ShouldBeNull();
        syntaxError.ShouldNotBeNullOrWhiteSpace();
        engine.AsyncAnalyzeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task TryResolveSqlTablesAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var engine = new RecordingAsyncSqlEngine();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            McpDataTargetResolver.TryResolveSqlTablesAsync("SELECT 1", engine, "default", cts.Token));
    }

    [Fact]
    public async Task ResolveInvokeApiTablesAsync_StatementEndpoint_UsesAsyncSqlEngine()
    {
        var engine = new RecordingAsyncSqlEngine();

        var tables = await McpDataTargetResolver.ResolveInvokeApiTablesAsync(
            """{"endpoint":"/api/v1/statement","parameters":{"query":"SELECT id FROM sales.public.orders"}}""",
            engine,
            "default");

        tables.ShouldBe([new TableIdentifier("sales", "public", "orders")]);
        engine.AsyncAnalyzeCalls.ShouldBe(1);
        engine.SyncAnalyzeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveInvokeApiTablesAsync_DataAndNonDataEndpoints_ResolveWithoutSqlEngine()
    {
        var engine = new RecordingAsyncSqlEngine();

        var dataTables = await McpDataTargetResolver.ResolveInvokeApiTablesAsync(
            """{"endpoint":"/api/v1/data/sales/public/orders","parameters":{"dataset":"sales.public.invoices"}}""",
            engine,
            "default");
        var catalogTables = await McpDataTargetResolver.ResolveInvokeApiTablesAsync(
            """{"endpoint":"/api/v1/catalog/tables"}""",
            engine,
            "default");
        var invalidTables = await McpDataTargetResolver.ResolveInvokeApiTablesAsync(
            "not-json",
            engine,
            "default");

        dataTables.ShouldBe([
            new TableIdentifier("sales", "public", "invoices"),
            new TableIdentifier("sales", "public", "orders")
        ]);
        catalogTables.ShouldBe([McpDataTargetResolver.ApiNoTable]);
        invalidTables.ShouldBeNull();
        engine.AsyncAnalyzeCalls.ShouldBe(0);
        engine.SyncAnalyzeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task AiDataGuardrailService_QuerySqlAndInvokeApiStatement_UseAsyncTargetResolution()
    {
        var engine = new RecordingAsyncSqlEngine();
        var executor = new StubQueryExecutor();
        var registry = new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoDataSwitch(false));
        var guardrail = new AiDataGuardrailService(
            registry,
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            NullAuditLogRepository.Instance,
            queryExecutor: executor,
            decisionPoint: PdpTestDoubles.AllowAll(),
            tableMetadataRepository: PdpTestDoubles.AnyTable(),
            sqlEngine: engine);
        var session = new McpSessionContext("sess-async", "agent-async", "tenant-a", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var sqlResult = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.QuerySql, """{"query":"SELECT id FROM sales.public.orders"}""", session.SessionId),
            session);
        var apiResult = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(
                McpDatasetTools.InvokeApi,
                """{"endpoint":"/api/v1/statement","parameters":{"query":"SELECT id FROM sales.public.orders"}}""",
                session.SessionId),
            session);

        sqlResult.IsSuccess.ShouldBeTrue();
        apiResult.IsSuccess.ShouldBeTrue();
        executor.ExecutionCount.ShouldBe(2);
        engine.AsyncAnalyzeCalls.ShouldBe(2);
        engine.SyncAnalyzeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task AiDataGuardrailService_QuerySql_PropagatesCancellation()
    {
        var engine = new RecordingAsyncSqlEngine();
        var registry = new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoDataSwitch(false));
        var guardrail = new AiDataGuardrailService(
            registry,
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            NullAuditLogRepository.Instance,
            queryExecutor: new StubQueryExecutor(),
            decisionPoint: PdpTestDoubles.AllowAll(),
            tableMetadataRepository: PdpTestDoubles.AnyTable(),
            sqlEngine: engine);
        var session = new McpSessionContext("sess-cancel", "agent-async", "tenant-a", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await guardrail.ExecuteToolWithGuardrailAsync(
                new McpToolCallRequest(McpDatasetTools.QuerySql, """{"query":"SELECT id FROM sales.public.orders"}""", session.SessionId),
                session,
                cts.Token));
    }

    private sealed class FixedDemoDataSwitch(bool enabled) : IDemoDataSwitch
    {
        public bool Enabled => enabled;
    }
}
