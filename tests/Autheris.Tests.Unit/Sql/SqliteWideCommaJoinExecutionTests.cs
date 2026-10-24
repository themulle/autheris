namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>
/// Issue #339: on the governed path every table becomes a derived table; SQLite pins the order of CROSS JOIN, so a wide comma join whose
/// equality graph is not in textual chain order ran near-cartesian and never finished. The join must render as a plain JOIN.
/// </summary>
public sealed class SqliteWideCommaJoinExecutionTests
{
    private const int Tables = 24;
    private const int Rows = 6;

    [Fact]
    public async Task WideCommaJoin_NonChainOrder_FinishesAndMatchesRawSql()
    {
        var env = new TestCompileEnv(TargetSqlDialect.Sqlite);
        for (var i = 1; i <= Tables; i++)
        {
            env.Table($"t{i}", "tenant_id", "a:INTEGER", "b:INTEGER", "tenant_id");
        }

        // Equality chain t(k).b = t(k+1).a, but the tables are listed interleaved (odd ones first, then even ones), so the textual
        // order is not the chain order; one literal filter is bound as a parameter.
        var order = Enumerable.Range(1, Tables).Where(i => i % 2 == 1).Concat(Enumerable.Range(1, Tables).Where(i => i % 2 == 0)).ToList();
        var from = string.Join(", ", order.Select(i => $"t{i}"));
        var where = string.Join(" AND ", Enumerable.Range(1, Tables - 1).Select(i => $"t{i}.b = t{i + 1}.a")) + " AND t1.a >= 1";
        var trinoSql = $"SELECT COUNT(*) FROM {from} WHERE {where}";

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            var ddl = new System.Text.StringBuilder();
            for (var i = 1; i <= Tables; i++)
            {
                ddl.Append($"CREATE TABLE t{i} (a INTEGER PRIMARY KEY, b INTEGER, tenant_id TEXT);");
                for (var r = 1; r <= Rows; r++)
                {
                    // a is the primary key (as in the sqllogictest corpus), so a good join order is a chain of key lookups; the second tenant has
                    // its own chain, which would double the result without the tenant row filter
                    ddl.Append($"INSERT INTO t{i} VALUES ({r}, {r}, 't1');");
                    ddl.Append($"INSERT INTO t{i} VALUES ({100 + r}, {100 + r}, 't2');");
                }
            }

            setup.CommandText = ddl.ToString();
            setup.ExecuteNonQuery();
        }

        long expected;
        using (var raw = connection.CreateCommand())
        {
            var tenantFilter = string.Join(" AND ", Enumerable.Range(1, Tables).Select(i => $"t{i}.tenant_id = 't1'"));
            raw.CommandText = trinoSql + " AND " + tenantFilter;
            expected = (long)raw.ExecuteScalar()!;
        }

        Assert.Equal(Rows, expected);   // 6 chains of tenant t1; the 6 chains of t2 are filtered out

        var compiled = env.Compile(trinoSql, tenant: "t1");
        using var command = connection.CreateCommand();
        CompiledSqlBinders.For(TargetSqlDialect.Sqlite).Bind(command, compiled, new Dictionary<string, object?>());

        // The guard interrupts the statement after the limit, so a regression fails with a TimeoutException instead of hanging.
        // No wall-clock assertion beyond the guard: it would be flaky on a loaded CI runner.
        var actual = await SqliteExecutionGuard.RunAsync(
            connection,
            TimeSpan.FromSeconds(5),
            async () => (long)(await command.ExecuteScalarAsync())!,
            CancellationToken.None);

        Assert.Equal(expected, actual);
    }
}
