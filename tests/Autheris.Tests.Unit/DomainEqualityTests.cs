using System.Collections.Generic;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class DomainEqualityTests
{
    [Fact]
    public void TableIdentifier_CaseInsensitiveEquality_AndHashing()
    {
        var t1 = new TableIdentifier("Finance", "Dbo", "Invoices");
        var t2 = new TableIdentifier("finance", "dbo", "invoices");

        (t1 == t2).ShouldBeTrue();
        t1.Equals(t2).ShouldBeTrue();
        t1.GetHashCode().ShouldBe(t2.GetHashCode());

        var dict = new Dictionary<TableIdentifier, string>
        {
            [t1] = "Value1"
        };

        dict.ContainsKey(t2).ShouldBeTrue();
        dict[t2].ShouldBe("Value1");
    }

    [Theory]
    [InlineData("..", false)]
    [InlineData("a..b", false)]
    [InlineData(".b.c", false)]
    [InlineData("a.b.", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("finance.dbo.invoices", true)]
    [InlineData("dbo.invoices", true)]
    public void TableIdentifier_TryParse_ValidatesSegments(string input, bool expectedSuccess)
    {
        TableIdentifier.TryParse(input, out var id).ShouldBe(expectedSuccess);
        if (expectedSuccess)
        {
            id.Domain.ShouldNotBeNullOrWhiteSpace();
            id.Schema.ShouldNotBeNullOrWhiteSpace();
            id.TableName.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Sid_CaseInsensitiveEquality_AndHashing()
    {
        var sid1 = new Sid("S-1-5-21-500-1000");
        var sid2 = new Sid("s-1-5-21-500-1000");

        (sid1 == sid2).ShouldBeTrue();
        sid1.Equals(sid2).ShouldBeTrue();
        sid1.GetHashCode().ShouldBe(sid2.GetHashCode());

        var set = new HashSet<Sid> { sid1 };
        set.Contains(sid2).ShouldBeTrue();
    }

    [Theory]
    [InlineData("mssql", DatabaseDialect.SqlServer)]
    [InlineData("SqlServer", DatabaseDialect.SqlServer)]
    [InlineData("sql_server", DatabaseDialect.SqlServer)]
    [InlineData("sqlite", DatabaseDialect.Sqlite)]
    [InlineData("sqlite3", DatabaseDialect.Sqlite)]
    [InlineData("postgresql", DatabaseDialect.PostgreSql)]
    [InlineData("postgres", DatabaseDialect.PostgreSql)]
    [InlineData("pgsql", DatabaseDialect.PostgreSql)]
    [InlineData("databricks", DatabaseDialect.Databricks)]
    [InlineData("spark", DatabaseDialect.Databricks)]
    [InlineData("oracle", DatabaseDialect.Oracle)]
    [InlineData("oracledb", DatabaseDialect.Oracle)]
    public void DialectParsing_SupportsAllFiveBackends(string sourceType, DatabaseDialect expectedDialect)
    {
        DatabaseDialectExtensions.ParseDialect(sourceType).ShouldBe(expectedDialect);

        var table = new Autheris.Domain.Model.Table
        {
            SourceType = sourceType
        };
        table.Dialect.ShouldBe(expectedDialect);

        var meta = new Autheris.Domain.Model.TableMetadata
        {
            Table = table
        };
        meta.Dialect.ShouldBe(expectedDialect);
    }
}
