namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Globalization;
using TrinoSqlEngine.Ast.Emit;
using Microsoft.Data.Sqlite;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>
/// Wunsch 4: Compiled output (the governed Compile path, bound through the SQLite binder) for constructs that need a dialect rewrite, executed against
/// SQLite and checked against .NET. Trino semantics: DOW/DAY_OF_WEEK is ISO, Monday = 1 … Sunday = 7.
/// </summary>
public sealed class AstSqliteExecutionTests
{
    private static object? Scalar(string trinoSql, string date, bool inline = false)
    {
        var env = new TestCompileEnv(TargetSqlDialect.Sqlite).Table("orders", "tenant_id", "id:INTEGER", "created_at", "tenant_id");
        var compiled = inline ? null : env.Compile(trinoSql, tenant: "t1");

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE orders (id INTEGER, created_at TEXT, tenant_id TEXT); INSERT INTO orders VALUES (1, '{date}', 't1');";
        command.ExecuteNonQuery();
        if (compiled is null)
        {
            // EXTRACT has no bound form on SQLite (the governed compiler rejects it, fail closed); the inline emission is what the
            // text consumers (virtual filters) render, so its rules are checked against .NET here.
            command.CommandText = InlineSql(trinoSql);
        }
        else
        {
            CompiledSqlBinders.For(TargetSqlDialect.Sqlite).Bind(command, compiled, new Dictionary<string, object?>());
        }

        return command.ExecuteScalar();
    }

    private static string InlineSql(string trinoSql)
    {
        var (tree, _) = new FastSqlEngine().Parse(trinoSql.AsMemory(), SqlTokenSecurityOptions.ForDialect(TargetSqlDialect.Sqlite));
        var ast = new TrinoSqlEngine.Ast.Builder.SqlAstBuilder(new TrinoSqlEngine.Ast.Builder.AstBuilderOptions()).BuildStatement(tree);
        return TrinoSqlEngine.Ast.Generators.SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.Sqlite).GenerateSql(ast);
    }

    [Theory]
    [InlineData("2024-01-01")] // Monday
    [InlineData("2024-01-06")] // Saturday
    [InlineData("2024-01-07")] // Sunday
    [InlineData("2024-02-29")]
    [InlineData("2024-12-31")]
    public void Extract_MatchesDotNet(string date)
    {
        var d = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        int isoDow = d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

        Convert.ToInt32(Scalar("SELECT EXTRACT(DOW FROM created_at) FROM orders", date, inline: true), CultureInfo.InvariantCulture).ShouldBe(isoDow);
        Convert.ToInt32(Scalar("SELECT EXTRACT(YEAR FROM created_at) FROM orders", date, inline: true), CultureInfo.InvariantCulture).ShouldBe(d.Year);
        Convert.ToInt32(Scalar("SELECT EXTRACT(MONTH FROM created_at) FROM orders", date, inline: true), CultureInfo.InvariantCulture).ShouldBe(d.Month);
        Convert.ToInt32(Scalar("SELECT EXTRACT(DAY FROM created_at) FROM orders", date, inline: true), CultureInfo.InvariantCulture).ShouldBe(d.Day);
        Convert.ToInt32(Scalar("SELECT EXTRACT(DAY_OF_YEAR FROM created_at) FROM orders", date, inline: true), CultureInfo.InvariantCulture).ShouldBe(d.DayOfYear);
        Convert.ToInt32(Scalar("SELECT EXTRACT(QUARTER FROM created_at) FROM orders", date, inline: true), CultureInfo.InvariantCulture).ShouldBe((d.Month + 2) / 3);
    }

    [Theory]
    [InlineData("SELECT substring(created_at FROM 6 FOR 2) FROM orders", "03")]
    [InlineData("SELECT substring(created_at FROM 9) FROM orders", "15")]
    [InlineData("SELECT position('-' IN created_at) FROM orders", "5")]
    [InlineData("SELECT position('x' IN created_at) FROM orders", "0")]
    [InlineData("SELECT trim(BOTH '2' FROM created_at) FROM orders", "024-03-15")]
    [InlineData("SELECT trim(LEADING '20' FROM created_at) FROM orders", "4-03-15")]
    [InlineData("SELECT trim(TRAILING '5' FROM created_at) FROM orders", "2024-03-1")]
    [InlineData("SELECT COUNT(*) FROM orders WHERE created_at IS DISTINCT FROM NULL", "1")]
    [InlineData("SELECT COUNT(*) FROM orders WHERE NULL IS NOT DISTINCT FROM NULL", "1")]
    [InlineData("SELECT COUNT(*) FROM orders WHERE created_at IS NOT DISTINCT FROM '2024-03-15'", "1")]
    public void StringSpecialForms_MatchTrino(string trinoSql, string expected)
    {
        Convert.ToString(Scalar(trinoSql, "2024-03-15"), CultureInfo.InvariantCulture).ShouldBe(expected);
    }
}
