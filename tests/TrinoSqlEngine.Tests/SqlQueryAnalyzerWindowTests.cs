using System.Linq;
using Xunit;

namespace TrinoSqlEngine.Tests;

/// <summary>#322 (SEC-RG-04): window PARTITION BY / ORDER BY columns are reported apart from the plain filter references.</summary>
[Trait("Category", "SqlGate")]
public class SqlQueryAnalyzerWindowTests
{
    private static Analysis.SqlQueryMetadata Analyze(string sql) => FastSqlEngine.Default.Analyze(sql.AsMemory());

    [Fact]
    public void WindowOrderAndPartition_NotInFilterReferences()
    {
        var meta = Analyze("SELECT row_number() OVER (PARTITION BY region ORDER BY amount) FROM t");

        Assert.Empty(meta.FilterColumnReferences!);
        var names = meta.WindowColumnReferences!.Select(r => r.ColumnName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "amount", "region" }, names);
    }

    [Fact]
    public void NamedWindow_IsCollected()
    {
        var meta = Analyze("SELECT rank() OVER w FROM t WINDOW w AS (PARTITION BY region ORDER BY amount)");

        Assert.Empty(meta.FilterColumnReferences!);
        Assert.Equal(2, meta.WindowColumnReferences!.Count);
    }

    [Fact]
    public void PlainOrderByAndWhere_StayFilterReferences()
    {
        var meta = Analyze("SELECT a FROM t WHERE b = 1 ORDER BY c");

        Assert.Equal(new[] { "b", "c" }, meta.FilterColumnReferences!.Select(r => r.ColumnName).OrderBy(x => x).ToArray());
        Assert.Empty(meta.WindowColumnReferences!);
    }
}
