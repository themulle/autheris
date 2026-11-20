using TrinoSqlEngine.Ast.Nodes;
using Xunit;

namespace TrinoSqlEngine.Tests;

[Trait("Category", "SqlGate")]
public sealed class SqlQueryAnalyzerIdentifierTests
{
    [Fact]
    public void Analyze_QuotedIdentifierWithDot_IsDistinctFromQualifiedName()
    {
        var meta = FastSqlEngine.Default.Analyze("SELECT * FROM \"a.b\" JOIN a.b ON 1 = 1".AsMemory());

        Assert.Equal(2, meta.ReferencedTables.Count);

        var singlePart = meta.ReferencedTables[0];
        Assert.Null(singlePart.Catalog);
        Assert.Null(singlePart.Schema);
        Assert.Equal("a.b", singlePart.TableName);
        Assert.True(singlePart.TableNameQuoted);
        Assert.False(singlePart.SchemaQuoted);
        Assert.NotNull(singlePart.QualifiedName);
        Assert.Single(singlePart.QualifiedName!.Parts);
        Assert.Equal(new SqlIdentifier("a.b", IsQuoted: true), singlePart.QualifiedName.Parts[0]);

        var twoPart = meta.ReferencedTables[1];
        Assert.Null(twoPart.Catalog);
        Assert.Equal("a", twoPart.Schema);
        Assert.Equal("b", twoPart.TableName);
        Assert.False(twoPart.SchemaQuoted);
        Assert.False(twoPart.TableNameQuoted);
        Assert.NotNull(twoPart.QualifiedName);
        Assert.Equal(2, twoPart.QualifiedName!.Parts.Count);
        Assert.Equal(new SqlIdentifier("a", IsQuoted: false), twoPart.QualifiedName.Parts[0]);
        Assert.Equal(new SqlIdentifier("b", IsQuoted: false), twoPart.QualifiedName.Parts[1]);
    }

    [Fact]
    public void Analyze_DoubledQuotes_AreUnescapedInTablePartsAndHelper()
    {
        Assert.Equal("a\"b", SqlIdentifierHelper.NormalizeIdentifier("\"a\"\"b\""));
        Assert.Equal(new SqlIdentifier("a\"b", IsQuoted: true), SqlIdentifierHelper.ParseIdentifier("\"a\"\"b\""));
        Assert.Equal(new SqlIdentifier("a`b", IsQuoted: true), SqlIdentifierHelper.ParseIdentifier("`a``b`"));

        var meta = FastSqlEngine.Default.Analyze("SELECT * FROM \"s\"\"1\".\"t\"\"2\"".AsMemory());

        var target = Assert.Single(meta.ReferencedTables);
        Assert.Equal("s\"1", target.Schema);
        Assert.Equal("t\"2", target.TableName);
        Assert.True(target.SchemaQuoted);
        Assert.True(target.TableNameQuoted);
        Assert.NotNull(target.QualifiedName);
        Assert.Equal(new SqlIdentifier("s\"1", IsQuoted: true), target.QualifiedName!.Parts[0]);
        Assert.Equal(new SqlIdentifier("t\"2", IsQuoted: true), target.QualifiedName.Parts[1]);
    }

    [Fact]
    public void Analyze_ThreePartNameWithQuotedCatalogDot_PreservesCatalogQuotedAndParts()
    {
        var meta = FastSqlEngine.Default.Analyze("SELECT * FROM \"cat.1\".sch.\"tbl\"".AsMemory());

        var target = Assert.Single(meta.ReferencedTables);
        Assert.Equal("cat.1", target.Catalog);
        Assert.True(target.CatalogQuoted);
        Assert.Equal("sch", target.Schema);
        Assert.False(target.SchemaQuoted);
        Assert.Equal("tbl", target.TableName);
        Assert.True(target.TableNameQuoted);
        Assert.NotNull(target.QualifiedName);
        Assert.Equal(3, target.QualifiedName!.Parts.Count);
    }
}
