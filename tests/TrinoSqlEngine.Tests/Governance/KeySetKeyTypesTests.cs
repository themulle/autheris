using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Governance;

/// <summary>SEC-RF107-07 (a), (c): the A-9 key type pair allowlist for every dialect that has a key loader.</summary>
[Trait("Category", "Security")]
public sealed class KeySetKeyTypesTests
{
    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "integer", "bigint")]
    [InlineData(TargetSqlDialect.PostgreSql, "int4", "int8")]
    [InlineData(TargetSqlDialect.PostgreSql, "uuid", "uuid")]
    [InlineData(TargetSqlDialect.PostgreSql, "character varying(20)", "varchar(30)")]
    [InlineData(TargetSqlDialect.PostgreSql, "varchar(20)", "char(20)")]
    [InlineData(TargetSqlDialect.PostgreSql, "numeric(10,0)", "decimal(20)")]
    [InlineData(TargetSqlDialect.Oracle, "NUMBER(10)", "NUMBER(12,0)")]
    [InlineData(TargetSqlDialect.Oracle, "VARCHAR2(20)", "VARCHAR2(40)")]
    [InlineData(TargetSqlDialect.Oracle, "VARCHAR2(20)", "NVARCHAR2(40)")]
    [InlineData(TargetSqlDialect.Oracle, "NVARCHAR2(20)", "NVARCHAR2(40)")]
    [InlineData(TargetSqlDialect.Sqlite, "INTEGER", "INTEGER")]
    [InlineData(TargetSqlDialect.Sqlite, "INTEGER", "BIGINT")]
    [InlineData(TargetSqlDialect.Sqlite, "BIGINT", "INT")]
    [InlineData(TargetSqlDialect.Sqlite, "VARCHAR(20)", "VARCHAR(20)")]
    public void AllowedPairs(TargetSqlDialect dialect, string key, string fk) =>
        Assert.True(KeySetKeyTypes.IsBindablePair(dialect, key, fk));

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "text", "text")]                    // unbounded
    [InlineData(TargetSqlDialect.PostgreSql, "varchar", "varchar")]              // unbounded
    [InlineData(TargetSqlDialect.PostgreSql, "numeric", "numeric")]              // unbounded
    [InlineData(TargetSqlDialect.PostgreSql, "uuid", "text")]
    [InlineData(TargetSqlDialect.PostgreSql, "bigint", "integer")]
    [InlineData(TargetSqlDialect.PostgreSql, "uniqueidentifier", "uniqueidentifier")]
    [InlineData(TargetSqlDialect.Oracle, "NUMBER", "NUMBER")]                    // scale unknown
    [InlineData(TargetSqlDialect.Oracle, "NUMBER(10,2)", "NUMBER(10,2)")]
    [InlineData(TargetSqlDialect.Oracle, "NVARCHAR2(20)", "VARCHAR2(40)")]
    [InlineData(TargetSqlDialect.Oracle, "VARCHAR2(20 CHAR)", "VARCHAR2(20 CHAR)")]
    [InlineData(TargetSqlDialect.Oracle, "RAW(16)", "RAW(16)")]
    [InlineData(TargetSqlDialect.Oracle, "DATE", "DATE")]
    [InlineData(TargetSqlDialect.Oracle, "FLOAT", "FLOAT")]
    [InlineData(TargetSqlDialect.Sqlite, "TEXT", "TEXT")]
    [InlineData(TargetSqlDialect.Sqlite, "BLOB", "BLOB")]
    [InlineData(TargetSqlDialect.Sqlite, "", "")]
    [InlineData(TargetSqlDialect.SqlServer, "decimal(10,0)", "int")]
    [InlineData(TargetSqlDialect.SqlServer, "int", "numeric(10,0)")]
    [InlineData(TargetSqlDialect.SqlServer, "nvarchar(", "nvarchar(10)")]
    [InlineData(TargetSqlDialect.SqlServer, "nvarchar(10))", "nvarchar(10)")]
    [InlineData(TargetSqlDialect.SqlServer, "nvarchar(0)", "nvarchar(10)")]
    [InlineData(TargetSqlDialect.SqlServer, "nvarchar(1,2)", "nvarchar(10)")]
    [InlineData(TargetSqlDialect.SqlServer, "int(4)", "int")]
    public void RejectedPairs(TargetSqlDialect dialect, string key, string fk) =>
        Assert.False(KeySetKeyTypes.IsBindablePair(dialect, key, fk));

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("a", null, true)]
    [InlineData("a", "A", true)]
    [InlineData("a", "b", false)]
    public void Collations(string? key, string? fk, bool expected) =>
        Assert.Equal(expected, KeySetKeyTypes.IsBindablePair(TargetSqlDialect.SqlServer, "int", "int", key, fk));

    [Fact]
    public void Classify_ReportsTheIntegerWidth()
    {
        Assert.Equal(new KeyTypeInfo(KeyTypeClass.WholeNumber, 2), KeySetKeyTypes.Classify(TargetSqlDialect.SqlServer, " SMALLINT "));
        Assert.Equal(KeyTypeClass.Unsupported, KeySetKeyTypes.Classify(TargetSqlDialect.SqlServer, new string('x', 100)).Class);
    }
}
