namespace Autheris.Tests.Unit.Sql;

using System;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>
/// Wunsch 4: AST output for constructs the legacy rewriter passes through unchanged (and SQLite rejects), executed against
/// SQLite and checked against .NET. Trino semantics: DOW/DAY_OF_WEEK is ISO, Monday = 1 … Sunday = 7.
/// </summary>
public sealed class AstSqliteExecutionTests
{
    private static object? Scalar(string trinoSql, string date)
    {
        string sql = new FastSqlEngine().GenerateGovernedSql(trinoSql, new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TargetDialect = TargetSqlDialect.Sqlite
        });

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE orders (id INTEGER, created_at TEXT, tenant_id TEXT); INSERT INTO orders VALUES (1, '{date}', 't1');";
        command.ExecuteNonQuery();
        command.CommandText = sql;
        return command.ExecuteScalar();
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

        Convert.ToInt32(Scalar("SELECT EXTRACT(DOW FROM created_at) FROM orders", date), CultureInfo.InvariantCulture).ShouldBe(isoDow);
        Convert.ToInt32(Scalar("SELECT EXTRACT(YEAR FROM created_at) FROM orders", date), CultureInfo.InvariantCulture).ShouldBe(d.Year);
        Convert.ToInt32(Scalar("SELECT EXTRACT(MONTH FROM created_at) FROM orders", date), CultureInfo.InvariantCulture).ShouldBe(d.Month);
        Convert.ToInt32(Scalar("SELECT EXTRACT(DAY FROM created_at) FROM orders", date), CultureInfo.InvariantCulture).ShouldBe(d.Day);
        Convert.ToInt32(Scalar("SELECT EXTRACT(DAY_OF_YEAR FROM created_at) FROM orders", date), CultureInfo.InvariantCulture).ShouldBe(d.DayOfYear);
        Convert.ToInt32(Scalar("SELECT EXTRACT(QUARTER FROM created_at) FROM orders", date), CultureInfo.InvariantCulture).ShouldBe((d.Month + 2) / 3);
    }

    [Theory]
    [InlineData("SELECT substring(created_at FROM 6 FOR 2) FROM orders", "03")]
    [InlineData("SELECT substring(created_at FROM 9) FROM orders", "15")]
    [InlineData("SELECT position('-' IN created_at) FROM orders", "5")]
    [InlineData("SELECT position('x' IN created_at) FROM orders", "0")]
    [InlineData("SELECT trim(BOTH '2' FROM created_at) FROM orders", "024-03-15")]
    [InlineData("SELECT trim(LEADING '20' FROM created_at) FROM orders", "4-03-15")]
    [InlineData("SELECT trim(TRAILING '5' FROM created_at) FROM orders", "2024-03-1")]
    public void StringSpecialForms_MatchTrino(string trinoSql, string expected)
    {
        Convert.ToString(Scalar(trinoSql, "2024-03-15"), CultureInfo.InvariantCulture).ShouldBe(expected);
    }
}
