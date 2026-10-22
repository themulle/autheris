namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #280: the typed <c>where</c> of a governed aggregation (eq, in, a gte/lt time window) reaches the database as bound gateway
/// parameters on a planned read. The scenario runs through the real DI container against a real PostgreSQL and a real SQL Server:
/// grouped MAX and COUNT return the exact rows, the rows of another tenant (the row-level security negative control, SEC-280-4) never
/// contribute, and a client parameter named like a gateway parameter is still rejected (SEC-280-1).
/// </summary>
public abstract class GovernedAggregationWhereParamsBase : IDisposable
{
    private const string Source = "agg280";
    private const string TableName = "agg280_events";
    private const string Tenant = "acme";

    private static readonly DateTime WindowStart = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);

    private WebApplicationFactory<Program>? _host;

    protected abstract DialectTarget? Target { get; }

    protected sealed record DialectTarget(string Provider, string SourceType, string ConnectionString, string? DefaultSchema, string Schema);

    /// <summary>
    /// Seed rows: (tenant, region, timestamp text, amount). Every group inside the window has five rows (the default minimum group size
    /// is five). Outside the window: an EU row exactly at the exclusive end (999) and one before the start (888). Another tenant has five
    /// EU rows with amount 5000 inside the window: if row-level security were bypassed, the EU maximum would be 5000.
    /// </summary>
    protected static IReadOnlyList<(string Tenant, string Region, string Ts, int Amount)> Rows()
    {
        var rows = new List<(string, string, string, int)>();
        for (var i = 0; i < 5; i++)
        {
            rows.Add((Tenant, "EU", $"2026-01-{11 + i:00}T12:00:00", (i + 1) * 10));
            rows.Add((Tenant, "US", $"2026-01-{11 + i:00}T12:00:00", i + 1));
            rows.Add((Tenant, "APAC", $"2026-01-{11 + i:00}T12:00:00", i == 4 ? 70 : 7));
            rows.Add(("other", "EU", $"2026-01-{11 + i:00}T12:00:00", 5000));
        }

        rows.Add((Tenant, "EU", "2026-01-20T00:00:00", 999));
        rows.Add((Tenant, "EU", "2026-01-09T23:59:59", 888));
        return rows;
    }

    /// <summary>The INSERT statement for <see cref="Rows"/>; <paramref name="timeSuffix"/> is appended to every timestamp text.</summary>
    protected static string InsertSql(string qualifiedTable, string stringPrefix, string timeSuffix)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"INSERT INTO {qualifiedTable} (id, tenant_id, region, ts, amount) VALUES ");
        var id = 1;
        foreach (var (tenant, region, ts, amount) in Rows())
        {
            if (id > 1)
            {
                sb.Append(',');
            }

            sb.Append(CultureInfo.InvariantCulture, $"({id++},{stringPrefix}'{tenant}',{stringPrefix}'{region}','{ts}{timeSuffix}',{amount})");
        }

        return sb.Append(';').ToString();
    }

    private async Task<bool> EnsureHostAsync()
    {
        if (_host is not null)
        {
            return true;
        }

        var target = Target;
        if (target is null)
        {
            return false;
        }

        _host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-agg280-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:WebSql:Enabled", "true");
            builder.UseSetting("Gateway:WebSql:DefaultDataSourceName", Source);
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:Provider", target.Provider);
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:ConnectionString", target.ConnectionString);
            if (target.DefaultSchema is not null)
            {
                builder.UseSetting($"Gateway:DataSources:Connections:{Source}:DefaultSchema", target.DefaultSchema);
            }
        });

        using var scope = _host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = TableName, TableName = TableName, SchemaName = target.Schema, SourceName = Source, SourceType = target.SourceType },
            Identifier = new TableIdentifier(Source, target.Schema, TableName),
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "ts", DataType = "timestamp" },
                new TableColumn { ColumnName = "amount", DataType = "int" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>()
        });
        return true;
    }

    private static ClaimsPrincipal User(string tenant) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-AGG280-E2E"),
                new Claim("tenant_id", tenant)
            ],
            "Test"));

    private static ColumnRef? Resolve(string column) => column.ToLowerInvariant() switch
    {
        "region" => new ColumnRef("region", ColumnValueKind.String),
        "ts" => new ColumnRef("ts", ColumnValueKind.DateTime),
        "amount" => new ColumnRef("amount", ColumnValueKind.Int),
        _ => null
    };

    private AggregationQuery Query(TreeFilter filter)
    {
        var table = new TableIdentifier(Source, Target!.Schema, TableName);
        return new AggregationQuery(
            table,
            GroupBy: ["region"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Max, "amount", "mx"),
                new AggregateSelection(AggregateFunction.Count, null, "n")
            ],
            Where: TreeFilterClauseBuilder.Build(filter, Resolve),
            OrderBy: [new TableOrderBy("region")],
            PreEvaluatedDecision: TableAccessDecision.Allowed(
                table,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["tenant_id"] = ColumnAccessLevel.Clear,
                    ["region"] = ColumnAccessLevel.Clear,
                    ["ts"] = ColumnAccessLevel.Clear,
                    ["amount"] = ColumnAccessLevel.Clear
                },
                null,
                hasUnconstrainedColumnAllow: true));
    }

    private static TreeFilter Window() => new TreeAndFilter(
    [
        new TreeComparison("ts", TreeFilterOperator.Gte, WindowStart),
        new TreeComparison("ts", TreeFilterOperator.Lt, WindowEnd)
    ]);

    private async Task<List<(string Region, long Count, long Max)>> RunAsync(TreeFilter filter, string tenant)
    {
        using var scope = _host!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IGovernedAggregationService>();
        var result = await service.ExecuteAggregationAsync(User(tenant), Query(filter));
        return result.Groups
            .Select(g => (
                Region: Convert.ToString(g.GroupKey.First(kv => kv.Key.Equals("region", StringComparison.OrdinalIgnoreCase)).Value, CultureInfo.InvariantCulture)!,
                Count: Convert.ToInt64(g.Aggregates["n"], CultureInfo.InvariantCulture),
                Max: Convert.ToInt64(g.Aggregates["mx"], CultureInfo.InvariantCulture)))
            .OrderBy(g => g.Region, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Aggregate_WhereEqAndTimeWindow_GroupedMax_ReturnsExpectedRows()
    {
        if (!await EnsureHostAsync()) return;

        // eq + gte/lt window: only acme's five EU rows inside the window (the boundary row and the early row are outside; tenant
        // "other" has EU rows with amount 5000 that row-level security must hide).
        var eq = await RunAsync(
            new TreeAndFilter(
            [
                new TreeComparison("region", TreeFilterOperator.Eq, "EU"),
                Window()
            ]),
            Tenant);
        eq.ShouldBe([("EU", 5L, 50L)]);

        // in + window: EU and US, never APAC.
        var inList = await RunAsync(
            new TreeAndFilter(
            [
                new TreeComparison("region", TreeFilterOperator.In, new List<object?> { "EU", "US" }),
                Window()
            ]),
            Tenant);
        inList.ShouldBe([("EU", 5L, 50L), ("US", 5L, 5L)]);

        // the same window without a region filter returns the three groups of the tenant.
        var all = await RunAsync(Window(), Tenant);
        all.ShouldBe([("APAC", 5L, 70L), ("EU", 5L, 50L), ("US", 5L, 5L)]);

        // the other tenant sees only its own rows (negative control in the other direction).
        var other = await RunAsync(new TreeAndFilter([new TreeComparison("region", TreeFilterOperator.Eq, "EU"), Window()]), "other");
        other.ShouldBe([("EU", 5L, 5000L)]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Issue280_ClientParameter_NamedLikeAGatewayParameter_IsStillRejected()
    {
        if (!await EnsureHostAsync()) return;

        using var scope = _host!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();
        var request = new GovernedSqlQueryRequest(
            $"SELECT id FROM {Target!.Schema}.{TableName} WHERE id = @__gql_tf_0",
            new Dictionary<string, object?> { ["__gql_tf_0"] = 1 },
            DataSourceName: Source,
            Transport: SqlTransports.Rest,
            PlannedTableRead: true);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await service.ExecuteQueryBufferedAsync(request, User(Tenant), new TenantId(Tenant)));

        ex.Message.ShouldContain("reserved gateway parameter name");
    }

    public virtual void Dispose() => _host?.Dispose();
}

public sealed class PostgreSqlGovernedAggregationWhereParamsTests : GovernedAggregationWhereParamsBase, IAsyncLifetime
{
    private PostgreSqlTestDatabase? _db;
    private DialectTarget? _target;
    protected override DialectTarget? Target => _target;

    public async Task InitializeAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
            if (!_db.IsAvailable)
            {
                return;
            }

            await using var conn = new NpgsqlConnection(_db.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE public.agg280_events (id integer, tenant_id text, region text, ts timestamptz, amount integer);" +
                InsertSql("public.agg280_events", string.Empty, "Z");
            await cmd.ExecuteNonQueryAsync();
            _target = new DialectTarget("PostgreSql", "PostgreSql", _db.ConnectionString, "public", "public");
        }
        catch (Exception ex)
        {
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
        }
    }

    public async Task DisposeAsync()
    {
        Dispose();
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }
}

public sealed class SqlServerGovernedAggregationWhereParamsTests : GovernedAggregationWhereParamsBase, IAsyncLifetime
{
    private SqlServerTestDatabase? _db;
    private DialectTarget? _target;
    protected override DialectTarget? Target => _target;

    public async Task InitializeAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        if (!_db.IsAvailable)
        {
            return;
        }

        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE dbo.agg280_events (id int NOT NULL, tenant_id nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, region nvarchar(20) NOT NULL, ts datetime2 NOT NULL, amount int NOT NULL);" +
            InsertSql("dbo.agg280_events", "N", string.Empty);
        await cmd.ExecuteNonQueryAsync();
        _target = new DialectTarget("SqlServer", "SqlServer", _db.ConnectionString, null, "dbo");
    }

    public async Task DisposeAsync()
    {
        Dispose();
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }
}
