namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Olap;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Paging;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Connectors;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class McpQuerySqlPagingTests
{
    private static readonly TableIdentifier OrdersTableId = new("sales", "public", "orders");

    private static readonly SeedRow[] SeedRows =
    [
        new(1, "row-1", "alice@secret.example"),
        new(2, "row-2", "bob@secret.example"),
        new(3, "row-3", "carol@secret.example"),
        new(4, "row-4", "dave@secret.example"),
        new(5, "row-5", "eve@secret.example")
    ];

    private static McpToolDefinition QuerySqlTool =>
        McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.QuerySql);

    private static McpSessionContext CreateSession(string userSid = "S-1-5-21-1055-1", string tenantId = "tenant-1") =>
        new(
            SessionId: "mcp-sess-1055",
            ServicePrincipalId: "agent-client",
            TenantId: tenantId,
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            UserSid: userSid,
            Roles: ["Consumer"],
            GroupSids: [],
            ClientIp: "127.0.0.1",
            IsReadOnly: false,
            AdditionalClaims: new Dictionary<string, string>());

    private static TableMetadata CreateOrdersMetadata(string source = "sales") => new()
    {
        Identifier = new TableIdentifier(source, "public", "orders"),
        Table = new Table
        {
            TableName = "orders",
            SchemaName = "public",
            SourceName = source,
            SourceType = "PostgreSQL",
            IsActive = true,
            DataSourceType = DataSourceType.Sql
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "name", DataType = "varchar" },
            new TableColumn { ColumnName = "email", DataType = "varchar" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = new MaskingRule { RuleType = "REDACT" }
        }
    };

    private static (McpToolExecutionHandler Handler, FakeTimeProvider Time, List<string> ExecutedSql) CreatePipeline(
        int maxResultRows = 2,
        bool maskEmail = false,
        int webSqlMaxAllowedRows = 0,
        bool withDemoSource = false,
        List<IReadOnlyList<object?>>? boundValues = null,
        List<CapturingSqlService>? capture = null,
        int maxOffset = 10_000,
        Action<IGovernedSqlExecutionService>? captureService = null,
        Action<WebSqlPageTokenCodec>? captureCodec = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 4, 0, 0, TimeSpan.Zero));
        var options = new GatewayOptions
        {
            Mcp = new McpOptions { MaxResultRows = maxResultRows },
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "sales",
                AllowedDataSources = withDemoSource ? ["sales", "demo"] : ["sales"],
                MaxAllowedRows = webSqlMaxAllowedRows,
                Paging = new WebSqlPagingOptions
                {
                    MaxOffset = maxOffset,
                    TokenLifetimeMinutes = 15
                }
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections =
                {
                    ["sales"] = new DataSourceConnectionOptions
                    {
                        Provider = "PostgreSQL",
                        ConnectionString = "Host=localhost;Database=sales;Username=u;Password=p"
                    }
                }
            }
        };

        if (withDemoSource)
        {
            options.DataSources.Connections["demo"] = new DataSourceConnectionOptions
            {
                Provider = "PostgreSQL",
                ConnectionString = "Host=localhost;Database=demo;Username=u;Password=p"
            };
        }

        var executedSql = new List<string>();
        var tableRepo = withDemoSource
            ? new InMemoryTableMetadataRepository(CreateOrdersMetadata(), CreateOrdersMetadata("demo"))
            : new InMemoryTableMetadataRepository(CreateOrdersMetadata());
        var consentResolution = new StubConsentResolutionService(table =>
            TableAccessDecision.Allowed(
                table,
                maskEmail
                    ? new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["id"] = ColumnAccessLevel.Clear,
                        ["name"] = ColumnAccessLevel.Clear,
                        ["email"] = ColumnAccessLevel.Mask
                    }
                    : new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase),
                hasUnconstrainedColumnAllow: !maskEmail));

        var connFactory = new RecordingConnectionFactory((sql, parameters) =>
        {
            executedSql.Add(sql);
            boundValues?.Add(parameters.ToArray());
            int limit = int.MaxValue;
            var limitMatch = Regex.Match(sql, @"LIMIT\s+(\d+)", RegexOptions.IgnoreCase);
            if (limitMatch.Success && int.TryParse(limitMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLimit))
            {
                limit = parsedLimit;
            }

            int offset = 0;
            for (int idx = parameters.Count - 1; idx >= 0; idx--)
            {
                if (parameters[idx] is long l)
                {
                    offset = (int)l;
                    break;
                }

                if (parameters[idx] is int i)
                {
                    offset = i;
                    break;
                }
            }

            bool desc = sql.Contains("DESC", StringComparison.OrdinalIgnoreCase);
            IEnumerable<SeedRow> ordered = desc ? SeedRows.OrderByDescending(r => r.Id) : SeedRows.OrderBy(r => r.Id);
            var fromIdx = sql.IndexOf(" FROM ", StringComparison.OrdinalIgnoreCase);
            var outerSelect = fromIdx > 0 ? sql[..fromIdx] : sql;
            bool querySelectsEmail = outerSelect.Contains("\"email\"", StringComparison.OrdinalIgnoreCase)
                || outerSelect.Contains("email", StringComparison.OrdinalIgnoreCase);
            var maskValue = parameters.OfType<string>().FirstOrDefault(v => v.StartsWith("***", StringComparison.Ordinal));
            // TrinoSqlEngine rewrites masked base tables as a derived subquery projecting CAST($n AS text) AS "email".
            bool sqlRedactsEmail = maskValue is not null
                && Regex.IsMatch(sql, @"CAST\(\$\d+\s+AS\s+text\)\s+AS\s+""email""", RegexOptions.IgnoreCase);

            var slice = ordered.Skip(offset).Take(limit).Select(r =>
                querySelectsEmail
                    ? new object?[] { r.Id, r.Name, sqlRedactsEmail ? maskValue : r.Email }
                    : new object?[] { r.Id, r.Name }).ToArray();

            var cols = querySelectsEmail ? new[] { "id", "name", "email" } : new[] { "id", "name" };
            return new PagingDbDataReader(cols, slice);
        });

        var env = new TestHostEnvironment("Development");
        var optsWrapper = Options.Create(options);
        var digester = new CompiledSqlAuditDigester(optsWrapper, null, env);
        var codec = new WebSqlPageTokenCodec(digester, time);

        var sqlService = GovernedSqlExecutionServiceFactory.Create(
            optsWrapper,
            NullAuditLogRepository.Instance,
            executionGuard: GovernedTestSupport.Guard,
            consentResolution: consentResolution,
            tableRepository: tableRepo,
            connectionFactory: connFactory,
            policyFactory: GovernedTestSupport.PolicyFactory(tableRepo, optsWrapper),
            typedPolicies: GovernedTestSupport.TypedPolicies(optsWrapper),
            consentRepository: new InMemoryConsentRepository(),
            auditDigester: digester,
            auditWriter: new InMemoryAuditWriter(),
            environment: env,
            pageTokenCodec: codec,
            logger: NullLogger<GovernedSqlExecutionService>.Instance);

        var capturing = new CapturingSqlService(sqlService);
        capture?.Add(capturing);
        captureService?.Invoke(sqlService);
        captureCodec?.Invoke(codec);
        var handler = new McpToolExecutionHandler(
            capturing,
            new StubDataQueryService(),
            new StubCatalogDiscoveryService(),
            new StubApiDispatcherService(),
            new InMemoryLineageGraphStore(),
            tableRepo,
            gatewayOptions: optsWrapper,
            logger: NullLogger<McpToolExecutionHandler>.Instance,
            timeProvider: time);

        return (handler, time, executedSql);
    }

    [Fact]
    public async Task QuerySql_DeterministicOrderBy_PagesThroughAllRowsWithoutDuplicatesOrGaps()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);
        var session = CreateSession();
        const string sql = "SELECT id, name FROM sales.public.orders ORDER BY id";

        var seenIds = new List<int>();
        string? cursor = null;
        var pageCount = 0;

        do
        {
            pageCount++;
            string argsJson = cursor is null
                ? JsonSerializer.Serialize(new { query = sql })
                : JsonSerializer.Serialize(new { query = sql, cursor });

            var json = await handler.ExecuteToolAsync(QuerySqlTool, argsJson, session);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var pageIds = root.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToList();
            seenIds.AddRange(pageIds);

            bool hasMore = root.GetProperty("hasMore").GetBoolean();
            if (pageCount < 3)
            {
                pageIds.Count.ShouldBe(2);
                hasMore.ShouldBeTrue();
                cursor = root.GetProperty("cursor").GetString();
                cursor.ShouldNotBeNullOrWhiteSpace();
            }
            else
            {
                pageIds.Count.ShouldBe(1);
                hasMore.ShouldBeFalse();
                root.TryGetProperty("cursor", out _).ShouldBeFalse();
                cursor = null;
            }
        }
        while (cursor is not null);

        pageCount.ShouldBe(3);
        seenIds.ShouldBe([1, 2, 3, 4, 5]);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("SELECT id, name, email FROM sales.public.orders ORDER BY id", true)]
    [InlineData("SELECT id, name FROM sales.public.orders ORDER BY id", false)]
    public async Task QuerySql_ReportsPolicyMaskingOfTheProjection(string sql, bool expected)
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 5, maskEmail: true);
        using var scope = McpResultMasking.Begin();

        await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql }), CreateSession());

        scope.Outcome.ShouldBe(expected);
    }

    [Fact]
    public async Task QuerySql_CursorOnNonDefaultCatalog_ReturnsNextPage()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2, withDemoSource: true);
        var session = CreateSession();
        const string sql = "SELECT id, name FROM demo.public.orders ORDER BY id";

        var firstJson = await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql }), session);
        using var firstDoc = JsonDocument.Parse(firstJson);
        firstDoc.RootElement.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        var cursor = firstDoc.RootElement.GetProperty("cursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();

        var secondJson = await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, cursor }), session);
        using var secondDoc = JsonDocument.Parse(secondJson);
        secondDoc.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToList()
            .ShouldBe([3, 4]);
    }

    [Fact]
    public async Task QuerySql_MaskedColumn_RemainsMaskedOnEveryPage()
    {
        var (handler, _, executedSql) = CreatePipeline(maxResultRows: 2, maskEmail: true);
        var session = CreateSession();
        const string sql = "SELECT id, name, email FROM sales.public.orders ORDER BY id";

        string? cursor = null;
        var totalRows = 0;

        do
        {
            string argsJson = cursor is null
                ? JsonSerializer.Serialize(new { query = sql })
                : JsonSerializer.Serialize(new { query = sql, cursor });

            var json = await handler.ExecuteToolAsync(QuerySqlTool, argsJson, session);
            json.ShouldNotContain("@secret.example");

            using var doc = JsonDocument.Parse(json);
            var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
            rows.Count.ShouldBeGreaterThan(0);
            foreach (var row in rows)
            {
                row.GetProperty("email").GetString().ShouldBe("***");
                totalRows++;
            }

            bool hasMore = doc.RootElement.GetProperty("hasMore").GetBoolean();
            cursor = hasMore ? doc.RootElement.GetProperty("cursor").GetString() : null;
        }
        while (cursor is not null);

        totalRows.ShouldBe(5);
        executedSql.Count.ShouldBe(3);
    }

    [Fact]
    public async Task QuerySql_TamperedExpiredCrossIdentityAndChangedSqlCursors_FailClosed()
    {
        var (handler, time, _) = CreatePipeline(maxResultRows: 2);
        var ownerSession = CreateSession("S-1-5-21-1055-OWNER", "tenant-1");
        const string sql = "SELECT id, name FROM sales.public.orders ORDER BY id";

        var firstJson = await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql }), ownerSession);
        using var firstDoc = JsonDocument.Parse(firstJson);
        var cursor = firstDoc.RootElement.GetProperty("cursor").GetString()!;

        // Tampered signature
        var tamperedCursor = cursor[..^1] + (cursor[^1] == 'A' ? 'B' : 'A');
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, cursor = tamperedCursor }), ownerSession));

        // Wrong user
        var otherUser = CreateSession("S-1-5-21-1055-OTHER", "tenant-1");
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, cursor }), otherUser));

        // Wrong tenant
        var otherTenant = CreateSession("S-1-5-21-1055-OWNER", "tenant-2");
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, cursor }), otherTenant));

        // Modified SQL text
        const string alteredSql = "SELECT id, name FROM sales.public.orders ORDER BY id DESC";
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = alteredSql, cursor }), ownerSession));

        // Malformed inputs
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, cursor = "" }), ownerSession));
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, cursor = new string('x', 1025) }), ownerSession));
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, """{"query":"SELECT id, name FROM sales.public.orders ORDER BY id","cursor":123}""", ownerSession));

        // Expired token
        time.Advance(TimeSpan.FromMinutes(16));
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, cursor }), ownerSession));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Cursor_Between512And1024_Accepted()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);
        var session = CreateSession("S-1-5-21-1055-OWNER", "tenant-1");
        const string sql = "SELECT id, name FROM sales.public.orders ORDER BY id";

        foreach (int length in new[] { 513, 600, 1024 })
        {
            var json = await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, cursor = new string('x', length) }), session);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("INVALID_PARAMS");
            // Passed the length gate and failed at token verification, not at the length check.
            doc.RootElement.GetProperty("error").GetProperty("message").GetString().ShouldBe("Invalid or expired continuation cursor.");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Cursor_Over1024_InvalidParams()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);
        var session = CreateSession("S-1-5-21-1055-OWNER", "tenant-1");
        const string sql = "SELECT id, name FROM sales.public.orders ORDER BY id";

        var json = await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, cursor = new string('x', 1025) }), session);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("INVALID_PARAMS");
        doc.RootElement.GetProperty("error").GetProperty("message").GetString().ShouldBe("Invalid continuation cursor.");
    }

    [Fact]
    public async Task QuerySql_LimitIsClampedAtMcpMaxResultRowsAndValidated()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);
        var session = CreateSession();
        const string sql = "SELECT id, name FROM sales.public.orders ORDER BY id";

        // limit > Mcp:MaxResultRows is clamped to 2
        var clampedJson = await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, limit = 50 }), session);
        using (var clampedDoc = JsonDocument.Parse(clampedJson))
        {
            clampedDoc.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(2);
            clampedDoc.RootElement.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        }

        // Smaller explicit limit=1 is honoured across pages
        var smallPage1Json = await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, limit = 1 }), session);
        using var smallPage1Doc = JsonDocument.Parse(smallPage1Json);
        smallPage1Doc.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(1);
        var smallCursor = smallPage1Doc.RootElement.GetProperty("cursor").GetString()!;

        // Page 2 without repeating limit keeps page size 1
        var smallPage2Json = await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, cursor = smallCursor }), session);
        using var smallPage2Doc = JsonDocument.Parse(smallPage2Json);
        smallPage2Doc.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(1);
        smallPage2Doc.RootElement.GetProperty("rows")[0].GetProperty("id").GetInt32().ShouldBe(2);

        // Changing limit mid-cursor fails closed
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, limit = 2, cursor = smallCursor }), session));

        // Invalid limits rejected
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, limit = 0 }), session));
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, limit = -1 }), session));
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, """{"query":"SELECT id FROM sales.public.orders","limit":"2"}""", session));

        // McpRowCap alignment
        McpRowCap.LimitArgument(McpDatasetTools.QuerySql).ShouldBe("limit");
        var trimmed = McpRowCap.TrimRows("""{"rows":[{"id":1},{"id":2},{"id":3}],"rowCount":3,"hasMore":false,"cursor":"c1","next":"c1"}""", 2);
        using var trimmedDoc = JsonDocument.Parse(trimmed);
        trimmedDoc.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(2);
        trimmedDoc.RootElement.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        trimmedDoc.RootElement.TryGetProperty("cursor", out _).ShouldBeFalse();
        trimmedDoc.RootElement.TryGetProperty("next", out _).ShouldBeFalse();
    }

    [Fact]
    public void QuerySqlToolDefinition_ExposesOptionalLimitAndCursorSchema()
    {
        var tool = QuerySqlTool;
        using var doc = JsonDocument.Parse(tool.InputJsonSchema);
        var props = doc.RootElement.GetProperty("properties");

        props.TryGetProperty("query", out _).ShouldBeTrue();
        props.TryGetProperty("limit", out var limitSchema).ShouldBeTrue();
        limitSchema.GetProperty("type").GetString().ShouldBe("integer");
        props.TryGetProperty("cursor", out var cursorSchema).ShouldBeTrue();
        cursorSchema.GetProperty("type").GetString().ShouldBe("string");
    }

    [Fact]
    public async Task QuerySql_NonPageableQueries_PreserveTruncationWithoutCursor_AndWebSqlMaxAllowedRowsClampsLimit()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 10, webSqlMaxAllowedRows: 2);
        var session = CreateSession();

        // Non-ORDER BY query capped at min(Mcp:MaxResultRows=10, WebSql:MaxAllowedRows=2) = 2 reports truncated:true, hasMore:true, pagingUnavailable:"order_by_required"
        var noOrderJson = await handler.ExecuteToolAsync(QuerySqlTool, """{"query":"SELECT id, name FROM sales.public.orders"}""", session);
        using (var noOrderDoc = JsonDocument.Parse(noOrderJson))
        {
            noOrderDoc.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(2);
            noOrderDoc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
            noOrderDoc.RootElement.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
            noOrderDoc.RootElement.GetProperty("pagingUnavailable").GetString().ShouldBe("order_by_required");
            noOrderDoc.RootElement.TryGetProperty("cursor", out _).ShouldBeFalse();
        }

        // Explicit LIMIT query exceeding cap preserves statement_has_limit and truncated:true without issuing a cursor.
        var explicitLimitJson = await handler.ExecuteToolAsync(QuerySqlTool, """{"query":"SELECT id, name FROM sales.public.orders ORDER BY id LIMIT 4","limit":10}""", session);
        using (var explicitLimitDoc = JsonDocument.Parse(explicitLimitJson))
        {
            explicitLimitDoc.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(2);
            explicitLimitDoc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
            explicitLimitDoc.RootElement.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
            explicitLimitDoc.RootElement.GetProperty("pagingUnavailable").GetString().ShouldBe("statement_has_limit");
            explicitLimitDoc.RootElement.TryGetProperty("cursor", out _).ShouldBeFalse();
        }

        // Federated execution refuses cursor continuation on both buffered and streaming entry points.
        var fedOptions = new GatewayOptions { WebSql = new WebSqlOptions { CrossSource = new CrossSourceOptions { Enabled = true } } };
        var fedOptsWrap = Options.Create(fedOptions);
        var federatedService = new FederatedDuckDbExecutionService(
            fedOptsWrap,
            NullAuditLogRepository.Instance,
            new CrossSourceQueryRouter(new InMemoryTableMetadataRepository(CreateOrdersMetadata()), fedOptions.WebSql.CrossSource, fedOptions.WebSql),
            new CrossSourcePlanner(TrinoSqlEngine.FastSqlEngine.Default),
            new FederatedStagingService(
                new InMemoryConnectorRegistry(),
                StubColumnMaskingProvider.Null,
                NullAuditLogRepository.Instance,
                fedOptsWrap,
                NullLogger<FederatedStagingService>.Instance),
            new DuckDbOlapEngine(fedOptions.DuckDbOlap, NullLogger<DuckDbOlapEngine>.Instance));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1055-1")], "Test"));
        var tenant = new TenantId("tenant-1");

        await Should.ThrowAsync<TrinoSqlEngine.Ast.Emit.SqlPagingUnsupportedException>(() =>
            federatedService.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest("SELECT 1", Transport: SqlTransports.Mcp, NextToken: "tok"),
                principal,
                tenant));
        await Should.ThrowAsync<TrinoSqlEngine.Ast.Emit.SqlPagingUnsupportedException>(() =>
            federatedService.ExecuteGovernedQueryAsync(
                new GovernedSqlQueryRequest("SELECT 1", Transport: SqlTransports.Mcp, Page: new SqlPageWindow(2, 2, 1)),
                principal,
                tenant,
                (_, _) => Task.CompletedTask));
    }

    private static void AssertInvalidParams(string json)
    {
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("isError", out _).ShouldBeFalse();
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("INVALID_PARAMS");
    }

    private sealed record SeedRow(int Id, string Name, string Email);

    private sealed class RecordingConnectionFactory(Func<string, IReadOnlyList<object?>, System.Data.Common.DbDataReader> execute) : ISqlConnectionFactory
    {
        public Task<System.Data.Common.DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
            => Task.FromResult<System.Data.Common.DbConnection>(new RecordingDbConnection(execute));
    }

    private sealed class RecordingDbConnection(Func<string, IReadOnlyList<object?>, System.Data.Common.DbDataReader> execute) : System.Data.Common.DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "sales";
        public override string DataSource => "localhost";
        public override string ServerVersion => "16.0";
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => new StubDbTransaction(this);
        protected override System.Data.Common.DbCommand CreateDbCommand() => new RecordingDbCommand(this, execute);
    }

    private sealed class RecordingDbCommand(
        System.Data.Common.DbConnection connection,
        Func<string, IReadOnlyList<object?>, System.Data.Common.DbDataReader> execute) : System.Data.Common.DbCommand
    {
        private readonly RecordingParameterCollection _parameters = new();
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; } = 30;
        public override System.Data.CommandType CommandType { get; set; } = System.Data.CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override System.Data.UpdateRowSource UpdatedRowSource { get; set; }
        protected override System.Data.Common.DbConnection? DbConnection { get; set; } = connection;
        protected override System.Data.Common.DbParameterCollection DbParameterCollection => _parameters;
        protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override System.Data.Common.DbParameter CreateDbParameter() => new RecordingDbParameter();
        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior)
            => execute(CommandText, _parameters.ValuesList);
    }

    private sealed class RecordingParameterCollection : System.Data.Common.DbParameterCollection
    {
        private readonly List<System.Data.Common.DbParameter> _items = [];
        public IReadOnlyList<object?> ValuesList => _items.Select(p => p.Value).ToList();
        public override int Count => _items.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { _items.Add((System.Data.Common.DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((System.Data.Common.DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((System.Data.Common.DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
        public override void Insert(int index, object value) => _items.Insert(index, (System.Data.Common.DbParameter)value);
        public override void Remove(object value) => _items.Remove((System.Data.Common.DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) RemoveAt(i); }
        protected override System.Data.Common.DbParameter GetParameter(int index) => _items[index];
        protected override System.Data.Common.DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, System.Data.Common.DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, System.Data.Common.DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class RecordingDbParameter : System.Data.Common.DbParameter
    {
        public override System.Data.DbType DbType { get; set; }
        public override System.Data.ParameterDirection Direction { get; set; } = System.Data.ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class StubDbTransaction(System.Data.Common.DbConnection connection) : System.Data.Common.DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
        protected override System.Data.Common.DbConnection DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class StubDataQueryService : IGovernedDataQueryService
    {
        public Task<DatasetQueryEnvelope> ExecuteQueryAsync(DatasetQueryRequest request, RequestContext context, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubApiDispatcherService : IApiDispatcherService
    {
        public Task<string> DescribeApiAsync(string endpoint, string? method = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> InvokeApiAsync(string endpoint, string method, IReadOnlyDictionary<string, object?>? parameters = null, object? body = null, RequestContext? context = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetOpenApiJsonAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEndpointsDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetMcpToolsDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetQueryGuideDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class PagingDbDataReader(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows) : System.Data.Common.DbDataReader
    {
        private int _index = -1;

        public override int FieldCount => columns.Count;
        public override bool HasRows => rows.Count > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override int Depth => 0;

        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(rows[_index][ordinal], CultureInfo.InvariantCulture);
        public override byte GetByte(int ordinal) => Convert.ToByte(rows[_index][ordinal], CultureInfo.InvariantCulture);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => Convert.ToChar(rows[_index][ordinal], CultureInfo.InvariantCulture);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
        public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(rows[_index][ordinal], CultureInfo.InvariantCulture);
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(rows[_index][ordinal], CultureInfo.InvariantCulture);
        public override double GetDouble(int ordinal) => Convert.ToDouble(rows[_index][ordinal], CultureInfo.InvariantCulture);
        public override Type GetFieldType(int ordinal) => rows.Count > 0 && rows[0][ordinal] is { } sample ? sample.GetType() : (ordinal == 0 ? typeof(int) : typeof(string));
        public override float GetFloat(int ordinal) => Convert.ToSingle(rows[_index][ordinal], CultureInfo.InvariantCulture);
        public override Guid GetGuid(int ordinal) => rows[_index][ordinal] is Guid g ? g : Guid.Parse(GetString(ordinal));
        public override short GetInt16(int ordinal) => Convert.ToInt16(rows[_index][ordinal], CultureInfo.InvariantCulture);
        public override int GetInt32(int ordinal) => Convert.ToInt32(rows[_index][ordinal], CultureInfo.InvariantCulture);
        public override long GetInt64(int ordinal) => Convert.ToInt64(rows[_index][ordinal], CultureInfo.InvariantCulture);
        public override string GetName(int ordinal) => columns[ordinal];

        public override int GetOrdinal(string name)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown column name.");
        }

        public override string GetString(int ordinal) => Convert.ToString(rows[_index][ordinal], CultureInfo.InvariantCulture) ?? string.Empty;
        public override object GetValue(int ordinal) => rows[_index][ordinal] ?? DBNull.Value;
        public override int GetValues(object[] values) => 0;
        public override bool IsDBNull(int ordinal) => rows[_index][ordinal] is null or DBNull;
        public override bool NextResult() => false;
        public override bool Read() => ++_index < rows.Count;
        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
    }
}
