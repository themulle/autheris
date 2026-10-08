namespace Autheris.Tests.Unit.OData;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OData;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Befund 4a.3: OData $orderby and $count. Ordering only by clear-text columns, the count is the total under the same
/// row filter (not the rows of the page), and a source that cannot order or count answers 501 instead of ignoring it.
/// </summary>
public sealed class ODataOrderByCountTests : IDisposable
{
    private static readonly TableIdentifier Invoices = new("erp", "main", "invoices");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"autheris-odata-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private static TableMetadata InvoiceMetadata(DataSourceType type = DataSourceType.Sql) => new()
    {
        Identifier = Invoices,
        Table = new Table { SourceName = "erp", SchemaName = "main", TableName = "invoices", DataSourceType = type, SourceType = "Sqlite" },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "region", DataType = "text" },
            new TableColumn { ColumnName = "amount", DataType = "integer" },
            new TableColumn { ColumnName = "iban", DataType = "text" }
        ]
    };

    private static readonly Dictionary<string, ColumnAccessLevel> Access = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = ColumnAccessLevel.Clear,
        ["region"] = ColumnAccessLevel.Clear,
        ["amount"] = ColumnAccessLevel.Clear,
        ["iban"] = ColumnAccessLevel.Mask
    };

    private void CreateDatabase()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE invoices (id INTEGER PRIMARY KEY, region TEXT NOT NULL, amount INTEGER NOT NULL, iban TEXT);
INSERT INTO invoices VALUES (1, 'CH', 300, 'CH01'), (2, 'DE', 100, 'DE02'), (3, 'CH', 200, 'CH03'), (4, 'CH', 50, 'CH04');";
        cmd.ExecuteNonQuery();
    }

    private SqlDataSourceExecutor CreateSqliteExecutor() => new(
        new SqlConnectionFactory(),
        Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["erp"] = new() { Provider = "Sqlite", ConnectionString = $"Data Source={_dbPath}" }
                }
            }
        }),
        NullLogger<SqlDataSourceExecutor>.Instance);

    private static DataSourceExecutionContext Context(int limit, int offset, IReadOnlyList<TableOrderBy>? orderBy, bool count)
    {
        var items = new Dictionary<string, object?>();
        if (orderBy != null) items[TableQueryItems.OrderBy] = orderBy;
        if (count) items[TableQueryItems.CountTotal] = true;
        return new DataSourceExecutionContext(
            SourceName: "erp",
            Metadata: InvoiceMetadata(),
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST")], "Test")),
            AccessDecision: TableAccessDecision.Allowed(Invoices, Access, rowFilterSql: "region = 'CH'"),
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id", "amount"],
            Limit: limit,
            Offset: offset,
            Items: items);
    }

    [Fact]
    public async Task SqlExecutor_OrdersAndCountsUnderTheRowFilter()
    {
        CreateDatabase();
        var context = Context(limit: 2, offset: 0, [new TableOrderBy("amount", Descending: true)], count: true);

        var rows = await CreateSqliteExecutor().ExecuteAsync(context);

        rows.Select(r => Convert.ToInt64(r["id"])).ShouldBe([1L, 3L]);           // CH rows by amount desc: 300, 200 (50 on page 2)
        context.Items[TableQueryItems.TotalCount].ShouldBe(3L);                  // DE row excluded by the row filter
    }

    [Fact]
    public async Task SqlExecutor_SecondPage_FollowsTheSameOrder()
    {
        CreateDatabase();
        var rows = await CreateSqliteExecutor().ExecuteAsync(Context(limit: 2, offset: 2, [new TableOrderBy("amount")], count: false));

        rows.Select(r => Convert.ToInt64(r["id"])).ShouldBe([1L]);               // ascending 50, 200 | 300
    }

    [Fact]
    public async Task SqlExecutor_OrderingByMaskedColumn_IsRejected()
    {
        CreateDatabase();
        await Should.ThrowAsync<System.Security.SecurityException>(() =>
            CreateSqliteExecutor().ExecuteAsync(Context(limit: 2, offset: 0, [new TableOrderBy("iban")], count: false)));
    }

    // ------------------------------------------------------------------ gateway

    private sealed class RecordingExecutor(DataSourceType type, long? totalCount) : IDataSourceExecutor
    {
        public DataSourceType SupportedType => type;
        public DataSourceExecutionContext? Last { get; private set; }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default)
        {
            Last = context;
            context.Items["RlsPushdownExecuted"] = true;
            if (totalCount.HasValue && context.Items.ContainsKey(TableQueryItems.CountTotal))
            {
                context.Items[TableQueryItems.TotalCount] = totalCount.Value;
            }

            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
        }
    }

    private static (GatewayExecutionService Service, RecordingExecutor Executor) Gateway(DataSourceType type = DataSourceType.Sql, long? totalCount = 42)
    {
        var executor = new RecordingExecutor(type, totalCount);
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(InvoiceMetadata(type));
        var cache = Substitute.For<IConsentCacheService>();
        cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(Invoices, Access, hasUnconstrainedColumnAllow: true));
        var service = new GatewayExecutionService(
            metadataRepo,
            Substitute.For<IConsentRepository>(),
            Substitute.For<IAuditLogRepository>(),
            Substitute.For<IConsentResolutionService>(),
            cache,
            Substitute.For<IColumnMaskingProvider>(),
            options: Options.Create(new GatewayOptions()),
            dataSourceExecutors: [executor]);
        return (service, executor);
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ANALYST"), new Claim("tenant_id", "erp")], "Test"));

    [Fact]
    public async Task Gateway_PassesOrderAndReturnsTheCountOfTheSource()
    {
        var (service, executor) = Gateway();

        var page = await service.ExecuteTablePageAsync(User(), Invoices,
            new TablePageRequest(First: 10, After: 0, OrderBy: [new TableOrderBy("AMOUNT", Descending: true)], IncludeTotalCount: true));

        page.TotalCount.ShouldBe(42);
        ((IReadOnlyList<TableOrderBy>)executor.Last!.Items[TableQueryItems.OrderBy]!).ShouldBe([new TableOrderBy("amount", true)]);
    }

    [Theory]
    [InlineData("iban")]
    [InlineData("does_not_exist")]
    public async Task Gateway_OrderingByMaskedOrUnknownColumn_IsRejectedWithTheSameMessage(string column)
    {
        var (service, executor) = Gateway();

        var ex = await Should.ThrowAsync<GatewayInvalidQueryException>(() => service.ExecuteTablePageAsync(User(), Invoices,
            new TablePageRequest(First: 10, After: 0, OrderBy: [new TableOrderBy(column)])));

        ex.Message.ShouldBe($"The column '{column}' in '$orderby' does not exist or cannot be used for ordering.");
        executor.Last.ShouldBeNull();
    }

    [Fact]
    public async Task Gateway_NonSqlSource_CannotOrderOrCount()
    {
        var (service, _) = Gateway(DataSourceType.HttpDeclarative);

        await Should.ThrowAsync<GatewayNotImplementedException>(() => service.ExecuteTablePageAsync(User(), Invoices,
            new TablePageRequest(First: 10, After: 0, IncludeTotalCount: true)));
    }

    [Fact]
    public async Task Gateway_SourceWithoutCount_Answers501InsteadOfThePageSize()
    {
        var (service, _) = Gateway(totalCount: null);

        await Should.ThrowAsync<GatewayNotImplementedException>(() => service.ExecuteTablePageAsync(User(), Invoices,
            new TablePageRequest(First: 10, After: 0, IncludeTotalCount: true)));
    }

    // ------------------------------------------------------------------ handler

    [Theory]
    [InlineData("amount desc, id", "amount:desc|id:asc")]
    [InlineData("region ASC", "region:asc")]
    public void Parser_AcceptsPropertiesWithDirection(string input, string expected)
    {
        ODataHandler.TryParseOrderBy(input, out var columns, out _).ShouldBeTrue();
        string.Join("|", columns.Select(c => $"{c.Column}:{(c.Descending ? "desc" : "asc")}")).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("amount sideways")]
    [InlineData("amount desc extra")]
    [InlineData("length(region)")]
    [InlineData("a/b")]
    [InlineData("amount,amount desc")]
    [InlineData("amount,")]
    public void Parser_RejectsAnythingButPropertyAndDirection(string input)
    {
        ODataHandler.TryParseOrderBy(input, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    private static (ODataHandler Handler, IGatewayExecutionService Execution) Handler(TableQueryPage page)
    {
        var execution = Substitute.For<IGatewayExecutionService>();
        execution.ExecuteTablePageAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<TablePageRequest>(), Arg.Any<CancellationToken>())
            .Returns(page);
        return (new ODataHandler(Substitute.For<ITableMetadataRepository>(), execution, NullLogger<ODataHandler>.Instance), execution);
    }

    [Fact]
    public async Task Handler_ReportsTheTotalCount_AndKeepsOrderAndCountInTheNextLink()
    {
        var rows = Enumerable.Range(1, 3).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = i }).ToList();
        var (handler, execution) = Handler(new TableQueryPage(rows, TableAccessDecision.Allowed(Invoices, Access), TotalCount: 7235));

        var result = await handler.ExecuteEntitySetQueryAsync(User(), "https://gw/odata/v4", Invoices, top: 2, skip: null, select: null,
            includeCount: true, headers: null, orderBy: "amount desc");

        result.StatusCode.ShouldBe(200);
        var payload = (IReadOnlyDictionary<string, object?>)result.Payload;
        payload["@odata.count"].ShouldBe(7235L);
        payload["@odata.nextLink"]!.ToString()!.ShouldContain("$orderby=amount%20desc");
        payload["@odata.nextLink"]!.ToString()!.ShouldContain("$count=true");
        await execution.Received(1).ExecuteTablePageAsync(Arg.Any<ClaimsPrincipal?>(), Invoices,
            Arg.Is<TablePageRequest>(r => r.IncludeTotalCount && r.First == 3 && r.OrderBy!.Single() == new TableOrderBy("amount", true)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_KeepsFilterInTheNextLink()
    {
        var rows = Enumerable.Range(1, 3).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = i }).ToList();
        var (handler, _) = Handler(new TableQueryPage(rows, TableAccessDecision.Allowed(Invoices, Access), TotalCount: 100));

        var result = await handler.ExecuteEntitySetQueryAsync(User(), "https://gw/odata/v4", Invoices, top: 2, skip: null, select: null,
            includeCount: false, headers: null, orderBy: null, filter: "status eq 'active'");

        result.StatusCode.ShouldBe(200);
        var payload = (IReadOnlyDictionary<string, object?>)result.Payload;
        payload.ShouldContainKey("@odata.nextLink");
        payload["@odata.nextLink"]!.ToString()!.ShouldContain("$filter=status%20eq%20%27active%27");
        payload["@odata.nextLink"]!.ToString()!.ShouldContain("$skip=2");
        payload["@odata.nextLink"]!.ToString()!.ShouldContain("$top=2");
    }

    [Fact]
    public async Task Handler_InvalidOrderBy_Returns400WithoutQuerying()
    {
        var (handler, execution) = Handler(new TableQueryPage([], TableAccessDecision.Allowed(Invoices, Access), null));

        var result = await handler.ExecuteEntitySetQueryAsync(User(), "https://gw/odata/v4", Invoices, null, null, null, false, null, orderBy: "amount sideways");

        result.StatusCode.ShouldBe(400);
        await execution.DidNotReceiveWithAnyArgs().ExecuteTablePageAsync(default, default, default!, default);
    }

    [Fact]
    public async Task Handler_NotImplementedBySource_Returns501()
    {
        var execution = Substitute.For<IGatewayExecutionService>();
        execution.ExecuteTablePageAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<TablePageRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<TableQueryPage>>(_ => throw new GatewayNotImplementedException("The total row count cannot be determined for this data source."));
        var handler = new ODataHandler(Substitute.For<ITableMetadataRepository>(), execution, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(User(), "https://gw/odata/v4", Invoices, null, null, null, true, null);

        result.StatusCode.ShouldBe(501);
    }
}

/// <summary>4a.3: the /$count segment answers the total as plain text and takes no query options.</summary>
public sealed class ODataCountSegmentTests
{
    private static IODataHandler HandlerReturning(ODataQueryResult result)
    {
        var handler = Substitute.For<IODataHandler>();
        handler.ExecuteEntitySetQueryAsync(
                Arg.Any<ClaimsPrincipal?>(), Arg.Any<string>(), Arg.Any<TableIdentifier>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return handler;
    }

    [Fact]
    public async Task CountSegment_ReturnsTheTotalAsPlainText()
    {
        var handler = HandlerReturning(new ODataQueryResult(true, 200, new Dictionary<string, object?> { ["@odata.count"] = 7235L, ["value"] = Array.Empty<object>() }));
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddLogging().BuildServiceProvider();

        var result = await Autheris.Api.Endpoints.ODataEndpoints.HandleCountRequestAsync("lwetem_prod", "md", "crane", handler, context);
        await result.ExecuteAsync(context);

        context.Response.ContentType.ShouldStartWith("text/plain");
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldBe("7235");
        await handler.Received(1).ExecuteEntitySetQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Any<string>(), new TableIdentifier("lwetem_prod", "md", "crane"), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<string?>(), true, Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CountSegment_WithQueryOption_Returns400()
    {
        var handler = HandlerReturning(new ODataQueryResult(true, 200, new Dictionary<string, object?>()));
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?$filter=a eq 1");
        context.Response.Body = new MemoryStream();
        context.RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddLogging().BuildServiceProvider();

        var result = await Autheris.Api.Endpoints.ODataEndpoints.HandleCountRequestAsync("lwetem_prod", "md", "crane", handler, context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(400);
    }
}
