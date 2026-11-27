using TrinoSqlEngine.Tests.Compiler;
using Xunit;

namespace TrinoSqlEngine.Tests.Paging;

public class KeysetShapeAnalyzerTests
{
    private static KeysetShape Analyze(string sql, params string[] key) =>
        KeysetShapeAnalyzer.Analyze(CompilerTestHelpers.Build(sql), KeysetTestEnv.Key(key));

    [Fact]
    public void Pk_SingleColumn_Eligible()
    {
        var shape = Analyze("SELECT id, name FROM crm.customers ORDER BY id", "id");
        Assert.Null(shape.Reason);
        Assert.Equal(["id"], shape.Columns.ToArray());
        Assert.Equal([false], shape.Descending.ToArray());
    }

    [Fact]
    public void Pk_CompositeMixedDirections_Eligible()
    {
        var shape = Analyze("SELECT * FROM crm.items c ORDER BY c.tenant_id DESC NULLS LAST, c.sku", "SKU", "tenant_id");
        Assert.Null(shape.Reason);
        Assert.Equal(["tenant_id", "SKU"], shape.Columns.ToArray());   // ORDER BY order, candidate spelling
        Assert.Equal([true, false], shape.Descending.ToArray());
    }

    [Fact]
    public void OrderByNonKeyColumn_NotEligible() =>
        Assert.Equal(KeysetUnavailableReasons.NotPrimaryKey, Analyze("SELECT id FROM crm.t ORDER BY name, id", "id").Reason);

    [Fact]
    public void OrderByMissingPkColumn_NotEligible() =>
        Assert.Equal(KeysetUnavailableReasons.NotPrimaryKey, Analyze("SELECT id FROM crm.t ORDER BY a", "a", "b").Reason);

    [Fact]
    public void OrderByRepeatedOrOrdinalOrWrongQualifier_NotEligible()
    {
        Assert.Equal(KeysetUnavailableReasons.NotPrimaryKey, Analyze("SELECT id FROM crm.t ORDER BY id, id", "id").Reason);
        Assert.Equal(KeysetUnavailableReasons.NotPrimaryKey, Analyze("SELECT id FROM crm.t ORDER BY 1", "id").Reason);
        Assert.Equal(KeysetUnavailableReasons.NotPrimaryKey, Analyze("SELECT id FROM crm.t x ORDER BY other.id", "id").Reason);
    }

    [Fact]
    public void Join_NotEligible() =>
        Assert.Equal(KeysetUnavailableReasons.Shape, Analyze("SELECT a.id FROM crm.a JOIN crm.b ON a.id = b.id ORDER BY a.id", "id").Reason);

    [Fact]
    public void SetOperation_NotEligible() =>
        Assert.Equal(KeysetUnavailableReasons.Shape, Analyze("SELECT id FROM crm.a UNION SELECT id FROM crm.b ORDER BY id", "id").Reason);

    [Fact]
    public void GroupByOrDistinct_NotEligible()
    {
        Assert.Equal(KeysetUnavailableReasons.Shape, Analyze("SELECT id FROM crm.a GROUP BY id ORDER BY id", "id").Reason);
        Assert.Equal(KeysetUnavailableReasons.Shape, Analyze("SELECT DISTINCT id FROM crm.a ORDER BY id", "id").Reason);
        Assert.Equal(KeysetUnavailableReasons.Shape, Analyze("SELECT id FROM crm.a GROUP BY id HAVING count(*) > 1 ORDER BY id", "id").Reason);
    }

    [Fact]
    public void WindowInProjection_NotEligible() =>
        Assert.Equal(KeysetUnavailableReasons.Window, Analyze("SELECT id, row_number() OVER (ORDER BY id) AS rn FROM crm.a ORDER BY id", "id").Reason);

    [Fact]
    public void AliasShadowsKey_NotEligible()
    {
        Assert.Equal(KeysetUnavailableReasons.AliasShadow, Analyze("SELECT name AS id FROM crm.a ORDER BY id", "id").Reason);
        Assert.Null(Analyze("SELECT id AS id, name FROM crm.a ORDER BY id", "id").Reason);
    }

    [Fact]
    public void RootCte_NotEligible() =>
        Assert.Equal(KeysetUnavailableReasons.Shape, Analyze("WITH x AS (SELECT id FROM crm.a) SELECT id FROM x ORDER BY id", "id").Reason);

    [Fact]
    public void SubqueryAndRootLimit_NotEligible()
    {
        Assert.Equal(KeysetUnavailableReasons.Shape, Analyze("SELECT id FROM (SELECT id FROM crm.a) s ORDER BY id", "id").Reason);
        Assert.Equal(KeysetUnavailableReasons.Shape, Analyze("SELECT id FROM crm.a ORDER BY id LIMIT 5", "id").Reason);
        Assert.Equal(KeysetUnavailableReasons.Shape, Analyze("SELECT id FROM crm.a", "id").Reason);
    }

    [Fact]
    public void MoreThanEightKeyColumns_NotEligible()
    {
        var key = Enumerable.Range(1, 9).Select(i => $"k{i}").ToArray();
        Assert.Equal(KeysetUnavailableReasons.KeyTooWide, Analyze($"SELECT * FROM crm.a ORDER BY {string.Join(", ", key)}", key).Reason);
        Assert.Equal(KeysetUnavailableReasons.Shape, Analyze("SELECT id FROM crm.a ORDER BY id").Reason);
    }
}
