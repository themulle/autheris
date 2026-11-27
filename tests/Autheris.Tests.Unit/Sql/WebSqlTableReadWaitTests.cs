namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Sql.ResultCache;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>WebSQL statements wait a bounded time for a free per-user and per-table read slot before they are throttled.</summary>
[Trait("Category", "Security")]
public sealed class WebSqlTableReadWaitTests
{
    private const string OrdersSql = "SELECT id, amount, tenant_id FROM orders";
    private static readonly TenantId Tenant = new("tenant_a");
    private static readonly TableIdentifier OrdersTable = new("default", "public", "orders");

    private static GovernedSqlExecutor CreateExecutor(TableReadConcurrencyGate gate, int waitMs)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default", ExecutionTimeoutSeconds = 30 },
            DataSources = new SqlDataSourceOptions
            {
                MaxConcurrentReadsPerUserAndTable = 4,
                TableReadWaitTimeoutMs = waitMs,
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = "Host=unused" },
                },
            },
        });
        var factory = new DelegatingConnectionFactory(() => new FakeDbConnection(() =>
        {
            var command = new FakeDbCommand();
            command.OnExecuteReader = (_, _) => Task.FromResult<DbDataReader>(Rows());
            return command;
        }));
        return new GovernedSqlExecutor(
            new GovernedSqlRewriter(options, sqlEngine: new FastSqlEngine()),
            options,
            new CapturingAuditWriter(),
            PassThroughExecutionGuard.Instance,
            factory,
            environment: new TestHostEnvironment("Development"),
            concurrencyGate: gate);
    }

    private static DbDataReader Rows()
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(string));
        table.Columns.Add("amount", typeof(decimal));
        table.Columns.Add("tenant_id", typeof(string));
        table.Rows.Add("1", 10m, "tenant_a");
        return table.CreateDataReader();
    }

    private static async Task<GovernedRewrite> RewriteAsync()
    {
        var compiled = await GovernedSqlExecutorX1Tests.CompileAsync("PostgreSQL", OrdersSql);
        var metadata = await FastSqlEngine.Default.AnalyzeAsync(OrdersSql.AsMemory(), null, CancellationToken.None);
        return new GovernedRewrite(compiled, [OrdersTable], "default", Metadata: metadata);
    }

    private static Task Run(GovernedSqlExecutor executor, GovernedRewrite rewrite, Func<Task> whileReading)
    {
        var values = new Dictionary<string, object?>();
        return executor.ExecuteRewrittenAsync(
            new GovernedSqlQueryRequest(OrdersSql, DataSourceName: "default", Transport: SqlTransports.WebSql),
            GovernedSqlExecutorX1Tests.Writer(),
            Tenant,
            rewrite,
            new DmlAuditContext(),
            values,
            async (_, _, _) => await whileReading().ConfigureAwait(false),
            CancellationToken.None);
    }

    [Fact]
    public async Task FiveStatementsLimitFour_AllSucceedWithinWait()
    {
        var gate = new TableReadConcurrencyGate(new FakeTimeProvider());
        var executor = CreateExecutor(gate, 5000);
        var rewrite = await RewriteAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var running = entered.Select(e => Run(executor, rewrite, async () =>
        {
            e.SetResult();
            await release.Task;
        })).ToArray();
        await Task.WhenAny(Task.WhenAll(entered.Select(e => e.Task)), Task.WhenAny(running)).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var done in running.Where(r => r.IsCompleted))
        {
            await done;
        }

        var fifth = Run(executor, rewrite, () => Task.CompletedTask);
        fifth.IsCompleted.ShouldBeFalse();
        release.SetResult();

        await fifth.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(running);
    }

    [Fact]
    public async Task WaitExpires_Throttled()
    {
        var time = new FakeTimeProvider();
        var gate = new TableReadConcurrencyGate(time);
        var executor = CreateExecutor(gate, 1000);
        var rewrite = await RewriteAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var running = entered.Select(e => Run(executor, rewrite, async () =>
        {
            e.SetResult();
            await release.Task;
        })).ToArray();
        await Task.WhenAny(Task.WhenAll(entered.Select(e => e.Task)), Task.WhenAny(running)).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var done in running.Where(r => r.IsCompleted))
        {
            await done;
        }

        var fifth = Run(executor, rewrite, () => Task.CompletedTask);
        fifth.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(1));

        await Should.ThrowAsync<GatewayThrottledException>(() => fifth);
        release.SetResult();
        await Task.WhenAll(running);
    }
}
