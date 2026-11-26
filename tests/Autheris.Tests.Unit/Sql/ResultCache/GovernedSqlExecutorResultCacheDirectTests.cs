namespace Autheris.Tests.Unit.Sql.ResultCache;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.ListParameters;
using Autheris.Application.Sql.ResultCache;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Sql;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>The executor half of the result cache with a hand-built rewrite: audit content of a hit, the metric-view filter and the eligibility mapping.</summary>
public sealed class GovernedSqlExecutorResultCacheDirectTests
{
    private const string OrdersSql = "SELECT id, amount, tenant_id FROM orders";

    private const string RecursiveSql =
        "WITH RECURSIVE t(id) AS (SELECT id FROM orders WHERE id = 1 UNION ALL SELECT id FROM t WHERE id < 3) SELECT id FROM t";

    private static readonly TenantId Tenant = new("tenant_a");

    private static readonly TableIdentifier OrdersTable = new("default", "public", "orders");

    [Fact]
    [Trait("Category", "Security")]
    public async Task Hit_audit_contains_caller_attribute_and_masked_columns_SECRC16()
    {
        using var direct = Direct.Create();
        var rewrite = await direct.RewriteAsync(OrdersSql, callerAttributes: true);

        await direct.RunAsync(rewrite);
        direct.Audit.Clear();
        var hit = await direct.RunAsync(rewrite);

        hit.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        direct.Executions.ShouldBe(1);
        var entry = direct.Audit.ShouldHaveSingleItem();
        entry.DetailsJson.ShouldContain("\"resultCache\":\"hit\"");
        entry.DetailsJson.ShouldContain("caller_attributes");
        entry.DetailsJson.ShouldContain("masked_columns");
        entry.DetailsJson.ShouldContain("sql.compiled_digest");
        entry.DetailsJson.ShouldContain("compiler_version");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Metric_view_group_filter_rows_not_leaked_via_cache()
    {
        using var direct = Direct.Create();
        direct.Source = () => Orders(("1", 1), ("2", 5), ("3", 2));
        var strict = await direct.RewriteAsync(OrdersSql, minGroupSize: 2);

        var first = await direct.RunAsync(strict);
        var hit = await direct.RunAsync(strict);
        var stricter = await direct.RunAsync(await direct.RewriteAsync(OrdersSql, minGroupSize: 3));

        first.Rows.ShouldBe(["2", "3"]);
        hit.Rows.ShouldBe(["2", "3"]);
        hit.FieldCount.ShouldBe(2);
        hit.Outcomes[0].Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        stricter.Outcomes[0].Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        stricter.Rows.ShouldBe(["2"]);
        direct.Executions.ShouldBe(2);
    }

    [Fact]
    public async Task Dml_statement_is_ineligible_and_never_planned_with_a_key()
    {
        using var direct = Direct.Create();
        var rewrite = await direct.RewriteAsync(OrdersSql);
        var outcomes = new List<WebSqlResultCacheOutcome>();

        var plan = await direct.PlanAsync(rewrite, outcomes, new DmlAuditContext { StatementType = TrinoSqlEngine.Analysis.SqlStatementType.Insert, Tables = ["orders"] });

        plan.ShouldNotBeNull();
        plan.Key.ShouldBeNull();
        plan.IneligibleReason.ShouldBe(WebSqlResultCacheReasons.NotReadOnly);
        outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Ineligible);
    }

    [Fact]
    public async Task Recursive_cte_is_ineligible()
    {
        using var direct = Direct.Create();
        var compiled = await GovernedSqlExecutorX1Tests.CompileAsync("PostgreSQL", RecursiveSql, allowRecursive: true);
        var metadata = await FastSqlEngine.Default.AnalyzeAsync(RecursiveSql.AsMemory(), null, CancellationToken.None);
        var rewrite = new GovernedRewrite(compiled, [OrdersTable], "default", Metadata: metadata);

        var plan = await direct.PlanAsync(rewrite, []);

        plan.ShouldNotBeNull();
        plan.IneligibleReason.ShouldBe(WebSqlResultCacheReasons.RecursiveCte);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Unknown_function_list_counts_as_volatile()
    {
        using var direct = Direct.Create();
        var compiled = await GovernedSqlExecutorX1Tests.CompileAsync("PostgreSQL", OrdersSql);
        var rewrite = new GovernedRewrite(compiled, [OrdersTable], "default", Metadata: null);

        var plan = await direct.PlanAsync(rewrite, []);

        plan.ShouldNotBeNull();
        plan.IneligibleReason.ShouldBe(WebSqlResultCacheReasons.VolatileFunction);
    }

    private static DbDataReader Orders(params (string Id, int Count)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(string));
        table.Columns.Add("amount", typeof(decimal));
        table.Columns.Add("cnt", typeof(int));
        foreach (var (id, count) in rows)
        {
            table.Rows.Add(id, 10m, count);
        }

        return table.CreateDataReader();
    }

    private sealed class DirectRun
    {
        public List<string> Rows { get; } = [];

        public List<WebSqlResultCacheOutcome> Outcomes { get; } = [];

        public int FieldCount { get; set; }
    }

    private sealed class Direct : IDisposable
    {
        private readonly CacheHarness _harness = new();
        private int _executions;

        private Direct(GatewayOptions options)
        {
            var opts = Microsoft.Extensions.Options.Options.Create(options);
            var factory = new DelegatingConnectionFactory(() => new FakeDbConnection(() =>
            {
                var command = new FakeDbCommand();
                command.OnExecuteReader = (_, _) =>
                {
                    Interlocked.Increment(ref _executions);
                    return Task.FromResult(Source());
                };
                return command;
            }));
            var writer = new CapturingAuditWriter();
            Audit = writer.Entries;
            Executor = new GovernedSqlExecutor(
                new GovernedSqlRewriter(opts, sqlEngine: new FastSqlEngine()),
                opts,
                writer,
                PassThroughExecutionGuard.Instance,
                factory,
                environment: new TestHostEnvironment("Development"),
                resultCache: _harness.Cache);
        }

        public GovernedSqlExecutor Executor { get; }

        public List<AuditLogEntry> Audit { get; }

        public Func<DbDataReader> Source { get; set; } = () => Orders(("1", 5), ("2", 5), ("3", 5));

        public int Executions => Volatile.Read(ref _executions);

        public static Direct Create() => new(new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default", ExecutionTimeoutSeconds = 30, AllowRecursiveCte = true },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = "Host=unused", ResultCacheTtlSeconds = 300 },
                },
            },
        });

        public async Task<GovernedRewrite> RewriteAsync(string sql, bool callerAttributes = false, int minGroupSize = 0)
        {
            var compiled = await GovernedSqlExecutorX1Tests.CompileAsync("PostgreSQL", sql);
            var metadata = await FastSqlEngine.Default.AnalyzeAsync(sql.AsMemory(), null, CancellationToken.None);
            return new GovernedRewrite(
                compiled,
                [OrdersTable],
                "default",
                Metadata: metadata,
                CallerAttributes: callerAttributes
                    ? new Dictionary<string, IReadOnlyList<CallerAttributeAuditEntry>> { ["orders"] = [new CallerAttributeAuditEntry("region", "applied", null)] }
                    : null,
                MaskedColumnsAudit: callerAttributes ? [new MaskedColumnAuditEntry("amount", ["pii"], "redact")] : null,
                MetricViewHiddenCountAlias: minGroupSize > 0 ? "cnt" : null,
                MetricViewMinGroupSize: minGroupSize);
        }

        public async Task<GovernedSqlExecutor.ResultCachePlan?> PlanAsync(GovernedRewrite rewrite, List<WebSqlResultCacheOutcome> outcomes, DmlAuditContext? dml = null)
        {
            var values = new Dictionary<string, object?>();
            return await Executor.PlanResultCacheAsync(
                Request(outcomes),
                GovernedSqlExecutorX1Tests.Writer(),
                Tenant,
                rewrite,
                dml ?? new DmlAuditContext(),
                values,
                OrdersSql,
                null,
                ClientListParameters.Shapes(values, new HashSet<string>()),
                CancellationToken.None);
        }

        public async Task<DirectRun> RunAsync(GovernedRewrite rewrite)
        {
            var run = new DirectRun();
            var user = GovernedSqlExecutorX1Tests.Writer();
            var request = Request(run.Outcomes);
            var values = new Dictionary<string, object?>();
            var plan = await Executor.PlanResultCacheAsync(
                request, user, Tenant, rewrite, new DmlAuditContext(), values, OrdersSql, null,
                ClientListParameters.Shapes(values, new HashSet<string>()), CancellationToken.None);
            await Executor.ExecuteRewrittenAsync(
                request,
                user,
                Tenant,
                rewrite,
                new DmlAuditContext(),
                values,
                async (reader, _, token) =>
                {
                    run.FieldCount = reader.FieldCount;
                    while (await reader.ReadAsync(token))
                    {
                        run.Rows.Add(Convert.ToString(reader.GetValue(0)) ?? string.Empty);
                    }
                },
                CancellationToken.None,
                plan);
            return run;
        }

        public void Dispose() => _harness.Dispose();

        private static GovernedSqlQueryRequest Request(List<WebSqlResultCacheOutcome> outcomes) =>
            new(OrdersSql, DataSourceName: "default", Transport: SqlTransports.WebSql)
            {
                ResultCache = new WebSqlResultCacheRequest(WebSqlResultCacheMode.Default, "json", outcomes.Add),
            };
    }
}
