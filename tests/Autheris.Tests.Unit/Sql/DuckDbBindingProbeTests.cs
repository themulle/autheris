namespace Autheris.Tests.Unit.Sql;

using System.Data;
using DuckDB.NET.Data;
using Shouldly;
using Xunit;

/// <summary>Pins how DuckDB.NET binds <c>$n</c> markers (the governed compiler's DuckDB marker style).</summary>
public sealed class DuckDbBindingProbeTests
{
    private static object? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = new DuckDBConnection("DataSource=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        return cmd.ExecuteScalar();
    }

    [Fact]
    public void DollarOrdinalMarkers_BindByNumericName()
    {
        Scalar("SELECT $1 || $2", ("1", "a"), ("2", "b")).ShouldBe("ab");
    }

    [Fact]
    public void ReusedMarker_BindsOneValue()
    {
        Scalar("SELECT $1 || $1", ("1", "x")).ShouldBe("xx");
    }

    [Fact]
    public void ProjectedParameter_Types()
    {
        Scalar("SELECT $1", ("1", 5)).ShouldBe(5);
        Scalar("SELECT length(CAST($1 AS VARCHAR))", ("1", "abc")).ShouldBe(3);
    }

    [Fact]
    public void Limit_AcceptsParameter()
    {
        Scalar("SELECT count(*) FROM (SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3) LIMIT $1", ("1", 1)).ShouldBe(3L);
    }
}
