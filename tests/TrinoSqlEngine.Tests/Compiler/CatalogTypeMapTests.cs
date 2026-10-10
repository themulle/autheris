using TrinoSqlEngine.Ast.Security;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>CR-ADG-42: the closed set of native column types the INSERT check option casts to.</summary>
public class CatalogTypeMapTests
{
    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "decimal(10,2)", "decimal(10,2)", false)]
    [InlineData(TargetSqlDialect.SqlServer, "DECIMAL( 10 , 2 )", "decimal(10,2)", false)]
    [InlineData(TargetSqlDialect.SqlServer, "NVarChar(max)", "nvarchar(max)", true)]
    [InlineData(TargetSqlDialect.SqlServer, "nvarchar(20)", "nvarchar(20)", true)]
    [InlineData(TargetSqlDialect.SqlServer, "datetime2(0)", "datetime2(0)", false)]
    [InlineData(TargetSqlDialect.PostgreSql, "text", "text", true)]
    [InlineData(TargetSqlDialect.PostgreSql, "character varying(30)", "character varying(30)", true)]
    [InlineData(TargetSqlDialect.PostgreSql, "timestamp(0) without time zone", "timestamp(0) without time zone", false)]
    [InlineData(TargetSqlDialect.PostgreSql, "numeric(18,2)", "numeric(18,2)", false)]
    [InlineData(TargetSqlDialect.Oracle, "varchar2(20)", "VARCHAR2(20)", true)]
    [InlineData(TargetSqlDialect.Oracle, "NUMBER(18,2)", "NUMBER(18,2)", false)]
    [InlineData(TargetSqlDialect.Oracle, "TIMESTAMP(3) WITH TIME ZONE", "TIMESTAMP(3) WITH TIME ZONE", false)]
    [InlineData(TargetSqlDialect.Oracle, "DATE", "DATE", false)]
    [InlineData(TargetSqlDialect.DuckDb, "VARCHAR", "varchar", true)]
    [InlineData(TargetSqlDialect.DuckDb, "DECIMAL(18,2)", "decimal(18,2)", false)]
    [InlineData(TargetSqlDialect.DuckDb, "DATE", "date", false)]
    public void KnownTypes_ResolveToTheNativeSpelling(TargetSqlDialect dialect, string catalogType, string expected, bool text)
    {
        Assert.True(CatalogTypeMap.TryResolve(dialect, catalogType, out string native, out bool isText));
        Assert.Equal(expected, native);
        Assert.Equal(text, isText);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, null)]
    [InlineData(TargetSqlDialect.SqlServer, "")]
    [InlineData(TargetSqlDialect.SqlServer, "geography")]
    [InlineData(TargetSqlDialect.SqlServer, "xml")]
    [InlineData(TargetSqlDialect.SqlServer, "nvarchar")]                    // a length is required: the CAST default (1) would truncate
    [InlineData(TargetSqlDialect.SqlServer, "nvarchar(0)")]
    [InlineData(TargetSqlDialect.SqlServer, "int(4)")]
    [InlineData(TargetSqlDialect.SqlServer, "decimal(10,2,3)")]
    [InlineData(TargetSqlDialect.SqlServer, "nvarchar(20); DROP TABLE x")]
    [InlineData(TargetSqlDialect.SqlServer, "nvarchar(20) with time zone")]
    [InlineData(TargetSqlDialect.PostgreSql, "citext")]
    [InlineData(TargetSqlDialect.PostgreSql, "regclass")]
    [InlineData(TargetSqlDialect.PostgreSql, "varchar(max)")]
    [InlineData(TargetSqlDialect.Oracle, "VARCHAR2")]
    [InlineData(TargetSqlDialect.Oracle, "VARCHAR2(20 CHAR)")]
    [InlineData(TargetSqlDialect.Oracle, "CLOB")]
    [InlineData(TargetSqlDialect.DuckDb, "STRUCT(a INTEGER)")]
    [InlineData(TargetSqlDialect.Databricks, "STRING")]
    [InlineData(TargetSqlDialect.Ansi, "integer")]
    public void UnknownOrUnsupportedTypes_HaveNoCast(TargetSqlDialect dialect, string? catalogType) =>
        Assert.False(CatalogTypeMap.TryResolve(dialect, catalogType, out _, out _));
}
