namespace Autheris.Tests.Unit.Endpoints;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class TableStreamEndpointsTests
{
    private static DefaultHttpContext CreateHttpContext(ClaimsPrincipal? principal = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.User = principal ?? new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "test-user"),
                new Claim("tenant", "default"),
                new Claim("sid", "S-1-5-21-test")
            ], "Bearer"));

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions()));
        context.RequestServices = services.BuildServiceProvider();

        return context;
    }

    private static TableMetadata CreateTableMetadata(string tableName = "orders", DatasetVisibility visibility = DatasetVisibility.Full)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", tableName),
            Table = new Table
            {
                TableName = tableName,
                SchemaName = "dbo",
                SourceName = "sales",
                SourceType = "PostgreSQL",
                Visibility = visibility
            },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "item", DataType = "varchar" },
                new TableColumn { ColumnName = "price", DataType = "numeric" }
            ]
        };
    }

    private static DbDataReader CreateDataTableReader(int rowCount)
    {
        var dt = new DataTable();
        dt.Columns.Add("id", typeof(int));
        dt.Columns.Add("item", typeof(string));
        dt.Columns.Add("price", typeof(decimal));

        for (int i = 1; i <= rowCount; i++)
        {
            dt.Rows.Add(i, $"Item {i}", (decimal)(i * 10.5));
        }

        return dt.CreateDataReader();
    }

    [Fact]
    public async Task HandleBulkStreamAsync_TableNotFound_Returns404()
    {
        var context = CreateHttpContext();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(null));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        var planner = new GovernedTableReadPlanner();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        var encoder = new KeysetCursorEncoder();
        var masking = Substitute.For<IColumnMaskingProvider>();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "nonexistent",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleBulkStreamAsync_HiddenTableNonAdmin_Returns404AntiEnumeration()
    {
        var context = CreateHttpContext();
        var hiddenTable = CreateTableMetadata(visibility: DatasetVisibility.Hidden);
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(hiddenTable));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        var planner = new GovernedTableReadPlanner();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        var encoder = new KeysetCursorEncoder();
        var masking = Substitute.For<IColumnMaskingProvider>();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleBulkStreamAsync_UnauthorizedTable_Returns404AntiEnumeration()
    {
        var context = CreateHttpContext();
        var table = CreateTableMetadata();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(table));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Denied(table.Identifier, "Forbidden")));

        var planner = new GovernedTableReadPlanner();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        var encoder = new KeysetCursorEncoder();
        var masking = Substitute.For<IColumnMaskingProvider>();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleBulkStreamAsync_OffsetQueryParam_RejectedWith400()
    {
        var context = CreateHttpContext();
        context.Request.QueryString = new QueryString("?offset=50");

        var repo = Substitute.For<ITableMetadataRepository>();
        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        var planner = new GovernedTableReadPlanner();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        var encoder = new KeysetCursorEncoder();
        var masking = Substitute.For<IColumnMaskingProvider>();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleBulkStreamAsync_Success_StreamsNdjsonRowsAndHeaders()
    {
        var context = CreateHttpContext();
        var table = CreateTableMetadata();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(table));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), null, true)));

        var planner = new GovernedTableReadPlanner();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();

        sqlExecution.ExecuteGovernedQueryAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<Func<DbDataReader, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var writer = callInfo.Arg<Func<DbDataReader, CancellationToken, Task>>();
                using var reader = CreateDataTableReader(5);
                await writer(reader, CancellationToken.None);
            });

        var encoder = new KeysetCursorEncoder();
        var masking = Substitute.For<IColumnMaskingProvider>();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers["X-Rows-Streamed"].ToString().ShouldBe("5");
        context.Response.Headers["X-Has-More"].ToString().ShouldBe("false");
        context.Response.Headers["X-Stream-Truncated"].ToString().ShouldBe("false");

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(5);

        using var firstDoc = JsonDocument.Parse(lines[0]);
        firstDoc.RootElement.GetProperty("id").GetInt32().ShouldBe(1);
        firstDoc.RootElement.GetProperty("item").GetString().ShouldBe("Item 1");
    }

    private static ClaimsPrincipal CreatePrincipal(string sub, string tenant) =>
        new(new ClaimsIdentity([new Claim("sub", sub), new Claim("tenant", tenant)], "Bearer"));

    private static void SetJsonBody(DefaultHttpContext context, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
    }

    [Fact]
    public async Task HandleBulkStreamAsync_EndpointIssuedCursor_DecodesInPlanner_AndReplayFailsClosed()
    {
        var table = CreateTableMetadata();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(table));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), null, true)));

        // One DI-style encoder (configured signing key) shared by the endpoint and the planner, as in production.
        var encoder = new KeysetCursorEncoder("endpoint-planner-shared-signing-key-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);

        var captured = new List<GovernedSqlQueryRequest>();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        sqlExecution.ExecuteGovernedQueryAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<Func<DbDataReader, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                captured.Add(callInfo.Arg<GovernedSqlQueryRequest>());
                var writer = callInfo.Arg<Func<DbDataReader, CancellationToken, Task>>();
                using var reader = CreateDataTableReader(5);
                await writer(reader, CancellationToken.None);
            });
        var masking = Substitute.For<IColumnMaskingProvider>();

        // Page 1: the endpoint issues nextCursor for the last emitted row (id = 2).
        var first = CreateHttpContext(CreatePrincipal("alice", "tenant-a"));
        SetJsonBody(first, "{\"limit\":2,\"select\":[\"id\",\"item\"]}");
        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            first, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        first.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        first.Response.Headers["X-Has-More"].ToString().ShouldBe("true");
        var nextCursor = first.Response.Headers["X-Next-Cursor"].ToString();
        nextCursor.ShouldNotBeNullOrWhiteSpace();

        async Task<DefaultHttpContext> ContinueAsync(ClaimsPrincipal principal, string bodyJson, string tableName = "orders")
        {
            var ctx = CreateHttpContext(principal);
            SetJsonBody(ctx, bodyJson);
            await TableStreamEndpoints.HandleBulkStreamAsync(
                "sales", "dbo", tableName,
                ctx, repo, pdp, planner, sqlExecution, encoder, masking,
                NullLoggerFactory.Instance, CancellationToken.None);
            return ctx;
        }

        var cursorJson = JsonSerializer.Serialize(nextCursor);

        // Page 2: same tenant, caller, table, filter and projection -> the planner decodes the endpoint's cursor.
        var second = await ContinueAsync(CreatePrincipal("alice", "tenant-a"), $"{{\"limit\":2,\"select\":[\"id\",\"item\"],\"cursor\":{cursorJson}}}");
        second.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        captured.Count.ShouldBe(2);
        captured[1].Parameters!["@cursor_0"].ShouldBe(2);

        // Other caller
        var otherCaller = await ContinueAsync(CreatePrincipal("mallory", "tenant-a"), $"{{\"limit\":2,\"select\":[\"id\",\"item\"],\"cursor\":{cursorJson}}}");
        otherCaller.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // Other tenant
        var otherTenant = await ContinueAsync(CreatePrincipal("alice", "tenant-b"), $"{{\"limit\":2,\"select\":[\"id\",\"item\"],\"cursor\":{cursorJson}}}");
        otherTenant.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // Other filter
        var otherFilter = await ContinueAsync(CreatePrincipal("alice", "tenant-a"), $"{{\"limit\":2,\"select\":[\"id\",\"item\"],\"filter\":{{\"field\":\"item\",\"op\":\"eq\",\"value\":\"x\"}},\"cursor\":{cursorJson}}}");
        otherFilter.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // Other projection
        var otherSelect = await ContinueAsync(CreatePrincipal("alice", "tenant-a"), $"{{\"limit\":2,\"select\":[\"id\",\"price\"],\"cursor\":{cursorJson}}}");
        otherSelect.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // Other table (same key shape)
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(CreateTableMetadata("invoices")));
        var otherTable = await ContinueAsync(CreatePrincipal("alice", "tenant-a"), $"{{\"limit\":2,\"select\":[\"id\",\"item\"],\"cursor\":{cursorJson}}}", "invoices");
        otherTable.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // None of the rejected continuations reached the database.
        captured.Count.ShouldBe(2);
    }

    [Fact]
    public async Task HandleBulkStreamAsync_ClientAbort_SettlesQuotaWithoutRequestToken()
    {
        var context = CreateHttpContext();
        var table = CreateTableMetadata();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(table));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), null, true)));

        var rateLimiter = Substitute.For<IRateLimiterService>();
        rateLimiter.ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(true, 5000, 0)));
        rateLimiter.SettleCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.ArgAt<CancellationToken>(4).ThrowIfCancellationRequested();
                return Task.FromResult(new CostQuotaResult(true, 4990, 0));
            });

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions()));
        services.AddSingleton(rateLimiter);
        context.RequestServices = services.BuildServiceProvider();

        using var aborted = new CancellationTokenSource();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        sqlExecution.ExecuteGovernedQueryAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<Func<DbDataReader, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                aborted.Cancel();
                return Task.FromException(new IOException("client aborted"));
            });

        // The original stream failure surfaces (not an OperationCanceledException from settlement) and the reservation is settled.
        await Should.ThrowAsync<IOException>(() => TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, new GovernedTableReadPlanner(), sqlExecution, new KeysetCursorEncoder(), Substitute.For<IColumnMaskingProvider>(),
            NullLoggerFactory.Instance, aborted.Token));

        await rateLimiter.Received(1).SettleCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    // ---- Issue #418: default slice must be admissible for every tier ----

    private sealed record SliceRun(DefaultHttpContext Context, IRateLimiterService Limiter, IConsumerUsageRecorder Recorder, IClientTierResolver Resolver, ClaimsPrincipal Principal, string Body, List<int> Reserved);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose() { }

        private sealed class CapturingLogger(List<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static ClientQuotaPolicy PolicyWithCapacity(ClientTier tier, int capacity) =>
        ClientQuotaPolicy.ForTier(tier) with { MaxTokensCapacity = capacity };

    private static async Task<SliceRun> RunSliceAsync(
        ClientQuotaPolicy policy,
        string? bodyJson,
        Action<DefaultHttpContext>? configure = null,
        CostQuotaResult? reserveResult = null,
        IRateLimiterService? limiter = null,
        ILoggerFactory? loggerFactory = null,
        List<GovernedSqlQueryRequest>? capturedQueries = null)
    {
        var context = CreateHttpContext();
        var table = CreateTableMetadata();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(table));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), null, true)));

        var reserved = new List<int>();
        var rateLimiter = limiter ?? Substitute.For<IRateLimiterService>();
        rateLimiter.ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Do<int>(reserved.Add), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(reserveResult ?? new CostQuotaResult(true, 1000, 0)));
        rateLimiter.SettleCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(true, 900, 0)));

        var resolver = Substitute.For<IClientTierResolver>();
        resolver.ResolveAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ClientQuotaContext("subject-418", policy.Tier, policy, "secret-scope")));
        var recorder = Substitute.For<IConsumerUsageRecorder>();

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions()));
        services.AddSingleton(rateLimiter);
        services.AddSingleton(resolver);
        services.AddSingleton(recorder);
        context.RequestServices = services.BuildServiceProvider();

        if (bodyJson != null)
        {
            var bytes = Encoding.UTF8.GetBytes(bodyJson);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
        }

        configure?.Invoke(context);

        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        sqlExecution.ExecuteGovernedQueryAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<Func<DbDataReader, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                capturedQueries?.Add(callInfo.Arg<GovernedSqlQueryRequest>());
                using var reader = CreateDataTableReader(3);
                await callInfo.Arg<Func<DbDataReader, CancellationToken, Task>>()(reader, CancellationToken.None);
            });

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, new GovernedTableReadPlanner(), sqlExecution, new KeysetCursorEncoder(), Substitute.For<IColumnMaskingProvider>(),
            loggerFactory ?? NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return new SliceRun(context, rateLimiter, recorder, resolver, context.User, text, reserved);
    }

    private static async Task AssertRejectedBeforeReservationAsync(SliceRun run)
    {
        run.Context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        await run.Limiter.DidNotReceive().ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>());
        run.Context.Response.Headers.ContainsKey("Retry-After").ShouldBeFalse();
        run.Context.Response.Headers.ContainsKey("X-RateLimit-Scope").ShouldBeFalse();
        run.Recorder.DidNotReceive().RecordUsage(Arg.Any<TableIdentifier>(), Arg.Any<TenantId>(), Arg.Any<Sid>(), ConsumerOutcome.DenyRateLimit, Arg.Any<long>(), Arg.Any<bool>(), Arg.Any<CallerContext?>());
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(ClientTier.Standard, 991)]
    [InlineData(ClientTier.Free, 91)]
    [InlineData(ClientTier.Enterprise, 10000)]
    public async Task Stream_ExplicitLimitAboveCapacityMinusBase_Returns400_WithoutReservation(ClientTier tier, int limit)
    {
        // I418-SC-03
        var run = await RunSliceAsync(ClientQuotaPolicy.ForTier(tier), $"{{\"limit\":{limit}}}");

        await AssertRejectedBeforeReservationAsync(run);
    }

    [Theory]
    [InlineData(ClientTier.Standard, 990, 1000)]
    [InlineData(ClientTier.Free, 90, 100)]
    [InlineData(ClientTier.Enterprise, 9990, 10000)]
    public async Task Stream_ExplicitLimitAtCapacityMinusBase_IsAdmitted(ClientTier tier, int limit, int expectedReservation)
    {
        var run = await RunSliceAsync(ClientQuotaPolicy.ForTier(tier), $"{{\"limit\":{limit}}}");

        run.Context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        run.Reserved.ShouldBe([expectedReservation]);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(10)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public async Task Stream_CapacityAtOrBelowBaseCost_FailsClosedWith400(int capacity)
    {
        // I418-SC-02
        var run = await RunSliceAsync(PolicyWithCapacity(ClientTier.Standard, capacity), null);

        await AssertRejectedBeforeReservationAsync(run);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(ClientTier.Free, 100)]
    [InlineData(ClientTier.Standard, 1000)]
    [InlineData(ClientTier.Enterprise, 5010)]
    [InlineData(ClientTier.Internal, 5010)]
    public async Task Stream_FreeAndStandardTier_DefaultSlice_IsAdmitted(ClientTier tier, int expectedReservation)
    {
        // I418-SC-02, I418-SC-05: omitted limit reserves min(5000, capacity - 10) + 10, never more than capacity.
        var policy = ClientQuotaPolicy.ForTier(tier);
        var run = await RunSliceAsync(policy, null);

        run.Context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        run.Reserved.ShouldBe([expectedReservation]);
        run.Reserved[0].ShouldBeLessThanOrEqualTo(policy.MaxTokensCapacity);
    }

    [Fact]
    public async Task Stream_ScopeCapacityOverride_UsesOverrideBasedMaximum()
    {
        var policy = PolicyWithCapacity(ClientTier.Standard, 500);

        var defaultRun = await RunSliceAsync(policy, null);
        defaultRun.Reserved.ShouldBe([500]);

        var tooLarge = await RunSliceAsync(policy, "{\"limit\":491}");
        await AssertRejectedBeforeReservationAsync(tooLarge);
        tooLarge.Body.ShouldContain("490");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Stream_RejectionDetail_ContainsOnlyMaxLimit_NoTierScopeOrCapacity()
    {
        // I418-SC-04
        var run = await RunSliceAsync(ClientQuotaPolicy.ForTier(ClientTier.Standard), "{\"limit\":123456}");

        using var doc = JsonDocument.Parse(run.Body);
        var detail = doc.RootElement.GetProperty("detail").GetString()!;
        detail.ShouldContain("990");
        run.Body.ShouldNotContain("123456");
        foreach (var forbidden in new[] { "Standard", "tier", "Tier", "scope", "1000", "refill", "capacity", "subject-418", "test-user" })
        {
            run.Body.ShouldNotContain(forbidden);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Stream_PolicyResolvedFromPrincipal_IgnoresRequestSuppliedTierOrScope()
    {
        // I418-SC-01
        var run = await RunSliceAsync(
            ClientQuotaPolicy.ForTier(ClientTier.Standard),
            "{\"limit\":991,\"tier\":\"Enterprise\",\"scope\":\"unlimited\"}",
            ctx =>
            {
                ctx.Request.Headers["X-Tier"] = "Enterprise";
                ctx.Request.Headers["X-RateLimit-Scope"] = "unlimited";
                ctx.Request.QueryString = new QueryString("?tier=Enterprise&scope=unlimited");
            });

        await AssertRejectedBeforeReservationAsync(run);
        await run.Resolver.Received(1).ResolveAsync(run.Principal, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Stream_ExhaustedBucket_Still429WithRetryAfter()
    {
        // I418-SC-06
        var run = await RunSliceAsync(ClientQuotaPolicy.ForTier(ClientTier.Standard), null, reserveResult: new CostQuotaResult(false, 0, 7));

        run.Context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        run.Context.Response.Headers["Retry-After"].ToString().ShouldBe("7");
        run.Reserved.ShouldBe([1000]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Stream_Rejection_UsesProblemDetailsBadRequest_NoLegacyBody()
    {
        // I418-SC-07
        var run = await RunSliceAsync(ClientQuotaPolicy.ForTier(ClientTier.Standard), "{\"limit\":991}");

        run.Context.Response.ContentType.ShouldNotBeNull().ShouldStartWith("application/problem+json");
        using var doc = JsonDocument.Parse(run.Body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("BAD_REQUEST");
        doc.RootElement.GetProperty("status").GetInt32().ShouldBe(400);
        doc.RootElement.TryGetProperty("error", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Stream_DefaultSlice_SameForInMemoryAndRedisLimiter()
    {
        // I418-SC-01 / limiter independence: the slice is derived from the policy only.
        var policy = ClientQuotaPolicy.ForTier(ClientTier.Standard);
        var inMemory = await RunSliceAsync(policy, null, limiter: Substitute.For<IRateLimiterService>());
        var redis = await RunSliceAsync(policy, null, limiter: Substitute.For<IRateLimiterService>());

        inMemory.Reserved.ShouldBe(redis.Reserved);
        inMemory.Reserved.ShouldBe([1000]);

        var rejectedA = await RunSliceAsync(policy, "{\"limit\":991}", limiter: Substitute.For<IRateLimiterService>());
        var rejectedB = await RunSliceAsync(policy, "{\"limit\":991}", limiter: Substitute.For<IRateLimiterService>());
        rejectedA.Context.Response.StatusCode.ShouldBe(rejectedB.Context.Response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Stream_OverflowCapacity_NoArithmeticOverflow()
    {
        // I418-SC-02, I418-SC-05
        var policy = PolicyWithCapacity(ClientTier.Internal, int.MaxValue);

        var defaultRun = await RunSliceAsync(policy, null);
        defaultRun.Reserved.ShouldBe([5010]);

        var cappedRun = await RunSliceAsync(policy, "{\"limit\":2147483647}");
        cappedRun.Context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        cappedRun.Reserved.ShouldBe([10010]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Stream_Rejection_DoesNotLogCapacityAtInformationOrAbove()
    {
        // I418-SC-08
        var logs = new CapturingLoggerFactory();
        var run = await RunSliceAsync(ClientQuotaPolicy.ForTier(ClientTier.Standard), "{\"limit\":991}", loggerFactory: logs);

        run.Context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        logs.Entries.Where(e => e.Level >= LogLevel.Information).ShouldAllBe(e => !e.Message.Contains("1000") && !e.Message.Contains("990"));
    }

    [Theory]
    [InlineData(ClientTier.Free, null, 91)]
    [InlineData(ClientTier.Standard, null, 991)]
    [InlineData(ClientTier.Standard, 500, 501)]
    [InlineData(ClientTier.Enterprise, null, 5001)]
    public async Task Stream_PlannedSqlLimit_IsEffectiveSlicePlusOne(ClientTier tier, int? limit, int expectedSqlLimit)
    {
        var captured = new List<GovernedSqlQueryRequest>();
        var body = limit is null ? null : $"{{\"limit\":{limit}}}";
        var run = await RunSliceAsync(ClientQuotaPolicy.ForTier(tier), body, capturedQueries: captured);

        run.Context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        captured.Single().Sql.ShouldEndWith($" LIMIT {expectedSqlLimit}");
    }

    [Fact]
    public void KeysetCursorContext_IsIndependentOfThePlannedLimit()
    {
        var metadata = CreateTableMetadata();
        var a = KeysetCursorContextFactory.ForBulkStream("t", metadata, metadata.PrimaryKeyColumns, "caller", new BulkTableStreamRequest { });
        var b = KeysetCursorContextFactory.ForBulkStream("t", metadata, metadata.PrimaryKeyColumns, "caller", new BulkTableStreamRequest(Limit: 90));
        var c = KeysetCursorContextFactory.ForBulkStream("t", metadata, metadata.PrimaryKeyColumns, "caller", new BulkTableStreamRequest(Limit: 9990));

        b.ShouldBe(a);
        c.ShouldBe(a);
    }
}
