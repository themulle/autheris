namespace Autheris.Tests.Unit.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Sql;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-B-02: the governed SQL table-read executor (planner + IGovernedSqlExecutionService).</summary>
[Trait("Category", "Security")]
public sealed class SqlTableReadExecutorTests
{
    private static readonly TableIdentifier Orders = new("shop", "main", "orders");

    private static TableMetadata Metadata(string dataType = "text") => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "shop", SchemaName = "main", TableName = "orders", DataSourceType = DataSourceType.Sql, SourceType = "Sqlite", IsActive = true },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "status", DataType = "text" },
            new TableColumn { ColumnName = "email", DataType = "text" },
            new TableColumn { ColumnName = "shape", DataType = dataType }
        ]
    };

    private static TableAccessDecision Decision(ColumnAccessLevel email = ColumnAccessLevel.Mask) => TableAccessDecision.Allowed(
        Orders,
        new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["status"] = ColumnAccessLevel.Clear,
            ["email"] = email,
            ["shape"] = ColumnAccessLevel.Clear
        },
        hasUnconstrainedColumnAllow: true);

    private static ClaimsPrincipal User(params Claim[] extra) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-EXEC"), new Claim("tenant_id", "t1"), .. extra], "Test"));

    private static DataSourceExecutionContext Context(
        TableAccessDecision? decision = null,
        IReadOnlyList<string>? fields = null,
        IReadOnlyDictionary<string, object?>? arguments = null,
        IDictionary<string, object?>? items = null,
        ClaimsPrincipal? principal = null,
        TableMetadata? metadata = null,
        int limit = 10,
        int offset = 0) =>
        new(
            SourceName: "shop",
            Metadata: metadata ?? Metadata(),
            Principal: principal ?? User(),
            AccessDecision: decision ?? Decision(),
            Arguments: arguments ?? new Dictionary<string, object?>(),
            RequestedFields: fields ?? ["id", "status"],
            Limit: limit,
            Offset: offset,
            Tenant: new TenantId("t1"),
            Items: items);

    private static (SqlTableReadExecutor Executor, IGovernedSqlExecutionService Governed) Create(params IReadOnlyDictionary<string, object?>[] rows)
    {
        var governed = Substitute.For<IGovernedSqlExecutionService>();
        governed.ExecuteQueryBufferedAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(ci => new GovernedSqlResult(ci.Arg<GovernedSqlQueryRequest>().Sql, [], rows, rows.Length, 0));
        return (new SqlTableReadExecutor(new GovernedTableReadPlanner(), () => governed), governed);
    }

    private static GovernedSqlQueryRequest Single(IGovernedSqlExecutionService governed) =>
        governed.ReceivedCalls().Select(c => (GovernedSqlQueryRequest)c.GetArguments()[0]!).Single();

    [Fact]
    public async Task Execute_RunsThePlannedStatementThroughTheGovernedService()
    {
        var (executor, governed) = Create(new Dictionary<string, object?> { ["id"] = 1L, ["status"] = "open" });

        var rows = await executor.ExecuteAsync(Context(limit: 25, offset: 5));

        rows.Count.ShouldBe(1);
        rows[0]["status"].ShouldBe("open");
        var request = Single(governed);
        request.Sql.ShouldBe("SELECT \"id\", \"status\" FROM shop.main.orders ORDER BY \"id\" OFFSET 5 ROWS LIMIT 25");
        request.Transport.ShouldBe(SqlTransports.Rest);
        await governed.ReceivedWithAnyArgs(1).ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Execute_ReportsRlsPushdown_AndInDbMaskingOnlyWhenAMaskedColumnIsSelected()
    {
        var (executor, _) = Create();
        var clear = Context(fields: ["id", "status"]);
        var masked = Context(fields: ["id", "email"]);

        await executor.ExecuteAsync(clear);
        await executor.ExecuteAsync(masked);

        clear.Items["RlsPushdownExecuted"].ShouldBe(true);
        clear.Items.ContainsKey("InDbColumnMaskingExecuted").ShouldBeFalse();
        masked.Items["RlsPushdownExecuted"].ShouldBe(true);
        masked.Items["InDbColumnMaskingExecuted"].ShouldBe(true);
    }

    [Fact]
    public async Task Execute_DeniedDecision_ThrowsBeforeAnyRead()
    {
        var (executor, governed) = Create();
        var denied = TableAccessDecision.Denied(Orders, "no consent");

        await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(Context(decision: denied)));

        await governed.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Execute_NonCanonicalClusterAdminClaim_ThrowsBeforeAnyRead()
    {
        var (executor, governed) = Create();
        var principal = User(new Claim(ClaimTypes.Role, "ClusterAdmin"));

        var ex = await Should.ThrowAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(Context(principal: principal)));

        ex.Message.ShouldContain("not canonical");
        await governed.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Execute_ArgumentOnAMaskedColumn_IsRejected_AsAnOracleInferenceAttempt()
    {
        var (executor, governed) = Create();
        var context = Context(arguments: new Dictionary<string, object?> { ["email"] = "a@b.c" });

        var ex = await Should.ThrowAsync<ArgumentException>(() => executor.ExecuteAsync(context));

        ex.Message.ShouldContain("email");
        await governed.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Execute_EqualityArgument_TravelsAsAClientParameter_NeverInTheText()
    {
        var (executor, governed) = Create();
        var context = Context(arguments: new Dictionary<string, object?> { ["status"] = "o'pen; DROP TABLE orders", ["limit"] = 5 });

        await executor.ExecuteAsync(context);

        var request = Single(governed);
        request.Sql.ShouldContain("WHERE \"status\" = @p_arg_0");
        request.Sql.ShouldNotContain("DROP TABLE");
        request.Parameters.ShouldNotBeNull();
        request.Parameters!["@p_arg_0"].ShouldBe("o'pen; DROP TABLE orders");
    }

    [Fact]
    public async Task Execute_ArgumentAndPushedDownFilter_AreCombinedWithAnd()
    {
        var (executor, governed) = Create();
        var items = new Dictionary<string, object?>
        {
            [TableQueryItems.Filter] = TableFilterClause.FromTrinoText("\"id\" > 3", ["id"])
        };
        var context = Context(arguments: new Dictionary<string, object?> { ["status"] = "open" }, items: items);

        await executor.ExecuteAsync(context);

        Single(governed).Sql.ShouldContain("WHERE (\"status\" = @p_arg_0) AND (\"id\" > 3)");
    }

    [Fact]
    public async Task Execute_CountTotal_PlansASecondCountStatement()
    {
        var governed = Substitute.For<IGovernedSqlExecutionService>();
        governed.ExecuteQueryBufferedAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(ci =>
            {
                var sql = ci.Arg<GovernedSqlQueryRequest>().Sql;
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = sql.StartsWith("SELECT COUNT(*)", StringComparison.Ordinal)
                    ? [new Dictionary<string, object?> { ["total_count"] = 42L }]
                    : [];
                return new GovernedSqlResult(sql, [], rows, rows.Count, 0);
            });
        var executor = new SqlTableReadExecutor(new GovernedTableReadPlanner(), () => governed);
        var context = Context(items: new Dictionary<string, object?> { [TableQueryItems.CountTotal] = true });

        await executor.ExecuteAsync(context);

        context.Items[TableQueryItems.TotalCount].ShouldBe(42L);
        await governed.ReceivedWithAnyArgs(2).ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Execute_TruncatedShortPage_FailsClosed()
    {
        var governed = Substitute.For<IGovernedSqlExecutionService>();
        governed.ExecuteQueryBufferedAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(new GovernedSqlResult("", [], [new Dictionary<string, object?> { ["id"] = 1L }], 1, 0, Truncated: true));
        var executor = new SqlTableReadExecutor(new GovernedTableReadPlanner(), () => governed);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => executor.ExecuteAsync(Context(limit: 10)));

        ex.ErrorCode.ShouldBe("ROW_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task Execute_SpatialColumn_FailsClosedWithTheUnsupportedColumnError()
    {
        var (executor, governed) = Create();

        var ex = await Should.ThrowAsync<GatewayUnsupportedColumnTypeException>(() =>
            executor.ExecuteAsync(Context(fields: ["id", "shape"], metadata: Metadata(dataType: "geometry"))));

        ex.ColumnName.ShouldBe("shape");
        await governed.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Execute_InvalidTenantClaim_ThrowsForbidden()
    {
        var (executor, _) = Create();
        var context = new DataSourceExecutionContext(
            "shop", Metadata(), new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "bad tenant; --")], "Test")),
            Decision(), new Dictionary<string, object?>(), ["id"]);

        var ex = await Should.ThrowAsync<GatewayForbiddenException>(() => executor.ExecuteAsync(context));

        ex.Message.ShouldBe("Invalid tenant identity.");
    }

    [Fact]
    public async Task Execute_OverSqlite_AppliesTheCompilerRowPolicyAndMask()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE orders (id INTEGER PRIMARY KEY, status TEXT, email TEXT, shape TEXT);
                INSERT INTO orders VALUES (1, 'open', 'a@x.example', NULL), (2, 'closed', 'b@x.example', NULL), (3, 'open', 'c@x.example', NULL);
                """;
            await setup.ExecuteNonQueryAsync();
        }

        var env = new TestCompileEnv(TrinoSqlEngine.TargetSqlDialect.Sqlite)
            .Table("orders", null, "id:INTEGER", "status", "email", "shape")
            .Policy("orders", SqliteGovernedSqlService.RowPolicy("status", "open"))
            .Mask("orders", "email", SqliteGovernedSqlService.Redact("email", "***"));
        var executor = new SqliteGovernedSqlService(connection, env).Executor();
        var context = Context(fields: ["id", "email"], items: new Dictionary<string, object?> { [TableQueryItems.CountTotal] = true });

        var rows = await executor.ExecuteAsync(context);

        rows.Select(r => Convert.ToInt64(r["id"])).ShouldBe([1L, 3L]);
        rows.ShouldAllBe(r => (string?)r["email"] == "***");
        context.Items[TableQueryItems.TotalCount].ShouldBe(2L);
        context.Items["InDbColumnMaskingExecuted"].ShouldBe(true);
    }

    [Fact]
    public void NormalizeReadValue_BinaryDatesAndTimes_UseInvariantRepresentations()
    {
        SqlTableReadExecutor.NormalizeReadValue(new byte[] { 1, 2, 3 }, "data").ShouldBe(Convert.ToBase64String([1, 2, 3]));
        SqlTableReadExecutor.NormalizeReadValue(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), "d").ShouldBe("2026-01-02T03:04:05.0000000Z");
        SqlTableReadExecutor.NormalizeReadValue(new DateOnly(2026, 1, 2), "d").ShouldBe("2026-01-02");
        SqlTableReadExecutor.NormalizeReadValue(new TimeSpan(1, 2, 3), "t").ShouldBe("01:02:03");
        SqlTableReadExecutor.NormalizeReadValue("2026-01-02T03:04:05", "s").ShouldBe("2026-01-02T03:04:05Z");
        SqlTableReadExecutor.NormalizeReadValue(DBNull.Value).ShouldBeNull();
    }

    [Fact]
    public void NormalizeReadValue_OversizedBinary_IsRejected()
    {
        var oversized = new byte[SqlTableReadExecutor.MaxAllowedBinaryBytes + 1];

        Should.Throw<SecurityException>(() => SqlTableReadExecutor.NormalizeReadValue(oversized, "huge_blob"));
    }
}
