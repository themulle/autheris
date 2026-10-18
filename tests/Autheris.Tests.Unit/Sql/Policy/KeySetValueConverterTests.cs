namespace Autheris.Tests.Unit.Sql.Policy;

using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>Issue #107 SEC-RF107-07 (a)-(c): closed pair allowlist, checked conversions, whole-site refusal; never truncate, round or drop.</summary>
[Trait("Category", "Security")]
public sealed class KeySetValueConverterTests
{
    private static KeySetValueConverter Create(TargetSqlDialect dialect, string key, string fk, string? keyCollation = null, string? fkCollation = null)
        => KeySetValueConverter.Create(dialect, key, fk, keyCollation, fkCollation).ShouldNotBeNull();

    private static bool Convert(KeySetValueConverter c, out object[] keys, params object?[] raw) => c.TryConvertAll(raw, out keys);

    [Fact]
    public void Integers_WidenToTheForeignKey_AsInt64()
    {
        var c = Create(TargetSqlDialect.SqlServer, "int", "bigint");

        Convert(c, out var keys, 1, 2L, (short)3, (byte)4, 5m).ShouldBeTrue();

        keys.ShouldBe([1L, 2L, 3L, 4L, 5L]);
        c.ParameterType.ShouldBe(SqlParameterType.Int64);
    }

    [Theory]
    [InlineData("smallint", 32768L)]
    [InlineData("smallint", -32769L)]
    [InlineData("int", 2147483648L)]
    [InlineData("tinyint", 256L)]
    [InlineData("tinyint", -1L)]
    public void IntegerOverflowOfTheForeignKeyType_RefusesTheWholeSite_SEC_RF107_07_b(string fkType, long value)
    {
        var c = Create(TargetSqlDialect.SqlServer, "tinyint", fkType);

        Convert(c, out var keys, 1L, value).ShouldBeFalse();

        keys.ShouldBeEmpty();   // nothing is returned for a refused site, never a partial list
    }

    [Fact]
    public void IntegerBoundaries_AreAccepted()
    {
        Convert(Create(TargetSqlDialect.SqlServer, "smallint", "smallint"), out _, (long)short.MinValue, (long)short.MaxValue).ShouldBeTrue();
        Convert(Create(TargetSqlDialect.SqlServer, "tinyint", "tinyint"), out _, 0L, 255L).ShouldBeTrue();
    }

    [Fact]
    public void NonIntegralNumbers_AreRefused_NeverRounded_SEC_RF107_07_b()
    {
        var c = Create(TargetSqlDialect.SqlServer, "int", "int");

        Convert(c, out _, 1.5m).ShouldBeFalse();
        Convert(c, out _, 1.5d).ShouldBeFalse();
        Convert(c, out _, true).ShouldBeFalse();
        Convert(c, out _, "1").ShouldBeFalse();
        Convert(c, out _, new object?[] { null }).ShouldBeFalse();
        Convert(c, out _, ulong.MaxValue).ShouldBeFalse();
    }

    [Fact]
    public void ExactNumeric_ScaleZero_IsNormalizedAndPrecisionChecked()
    {
        var c = Create(TargetSqlDialect.Oracle, "number(10,0)", "number(5,0)");

        Convert(c, out var keys, 12345m, 5.00m, 7L).ShouldBeTrue();

        keys.ShouldBe([12345m, 5m, 7m]);
        keys[1].ToString().ShouldBe("5");   // trailing zeros of the scale are removed
        c.ParameterType.ShouldBe(SqlParameterType.Decimal);
        Convert(c, out _, 123456m).ShouldBeFalse();     // exceeds the precision of the foreign key
        Convert(c, out _, 5.5m).ShouldBeFalse();
    }

    [Fact]
    public void ExactNumeric_SqlServerDefaultPrecision_Is18()
    {
        var c = Create(TargetSqlDialect.SqlServer, "decimal(18)", "decimal");

        Convert(c, out _, 999999999999999999m).ShouldBeTrue();
        Convert(c, out _, 1000000000000000000m).ShouldBeFalse();
    }

    [Fact]
    public void Text_OverLength_RefusesTheWholeSite_SEC_RF107_07_b()
    {
        var c = Create(TargetSqlDialect.SqlServer, "varchar(50)", "varchar(5)");

        Convert(c, out var keys, "abcde", "ab").ShouldBeTrue();
        keys.ShouldBe(["abcde", "ab"]);
        c.ParameterType.ShouldBe(SqlParameterType.String);

        Convert(c, out var refused, "abcde", "abcdef").ShouldBeFalse();
        refused.ShouldBeEmpty();
        Convert(c, out _, 12).ShouldBeFalse();
    }

    [Fact]
    public void VarcharToNvarchar_IsAllowed_NvarcharToVarcharIsRefused_SEC_RF107_07_a()
    {
        KeySetValueConverter.Create(TargetSqlDialect.SqlServer, "varchar(10)", "nvarchar(10)").ShouldNotBeNull();
        KeySetValueConverter.Create(TargetSqlDialect.SqlServer, "nvarchar(10)", "varchar(10)").ShouldBeNull();
        KeySetValueConverter.Create(TargetSqlDialect.Oracle, "nvarchar2(10)", "varchar2(10)").ShouldBeNull();
    }

    [Theory]
    [InlineData("int", "varchar(10)")]
    [InlineData("varchar(10)", "int")]
    [InlineData("float", "float")]
    [InlineData("datetime2", "datetime2")]
    [InlineData("varchar(max)", "varchar(max)")]
    [InlineData("varbinary(16)", "varbinary(16)")]
    [InlineData("bigint", "int")]
    [InlineData("uniqueidentifier", "varchar(36)")]
    public void PairsOutsideTheAllowlist_AreNotBindable_SEC_RF107_07_a(string key, string fk)
        => KeySetValueConverter.Create(TargetSqlDialect.SqlServer, key, fk).ShouldBeNull();

    [Fact]
    public void CollationMismatch_IsNotBindable_SEC_RF107_07_c()
    {
        KeySetValueConverter.Create(TargetSqlDialect.SqlServer, "varchar(10)", "varchar(10)", "Latin1_General_CI_AS", "Latin1_General_BIN2").ShouldBeNull();
        KeySetValueConverter.Create(TargetSqlDialect.SqlServer, "varchar(10)", "varchar(10)", "latin1_general_ci_as", "Latin1_General_CI_AS").ShouldNotBeNull();
        KeySetValueConverter.Create(TargetSqlDialect.SqlServer, "varchar(10)", "varchar(10)", null, "Latin1_General_BIN2").ShouldNotBeNull();   // only a differing pair is refused
    }

    [Fact]
    public void Uuid_IsBoundAsCanonicalLowerCaseString_SEC_RF107_07_b()
    {
        var c = Create(TargetSqlDialect.SqlServer, "uniqueidentifier", "uniqueidentifier");
        var guid = Guid.Parse("3F2504E0-4F89-41D3-9A0C-0305E82C3301");

        Convert(c, out var keys, guid, "3f2504e0-4f89-41d3-9a0c-0305e82c3301").ShouldBeTrue();

        keys.ShouldBe(["3f2504e0-4f89-41d3-9a0c-0305e82c3301", "3f2504e0-4f89-41d3-9a0c-0305e82c3301"]);
        c.ParameterType.ShouldBe(SqlParameterType.String);
    }

    [Theory]
    [InlineData("3F2504E0-4F89-41D3-9A0C-0305E82C3301")]       // upper case is not canonical
    [InlineData("{3f2504e0-4f89-41d3-9a0c-0305e82c3301}")]
    [InlineData("3f2504e04f8941d39a0c0305e82c3301")]
    [InlineData(" 3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    [InlineData("not-a-uuid")]
    [InlineData("")]
    public void NonCanonicalUuidString_RefusesTheWholeSite(string text)
        => Convert(Create(TargetSqlDialect.PostgreSql, "uuid", "uuid"), out _, "3f2504e0-4f89-41d3-9a0c-0305e82c3301", text).ShouldBeFalse();

    [Fact]
    public void Sqlite_AnyInteger_IsInt64Wide()
        => Convert(Create(TargetSqlDialect.Sqlite, "integer", "int"), out var keys, long.MaxValue).ShouldBeTrue();

    [Fact]
    public void EmptySet_Converts_ToAnEmptySet()
    {
        Convert(Create(TargetSqlDialect.SqlServer, "int", "int"), out var keys).ShouldBeTrue();
        keys.ShouldBeEmpty();
    }

    [Fact]
    public void HostileText_IsKeptVerbatim_NeverEscapedOrTruncated()
    {
        var c = Create(TargetSqlDialect.PostgreSql, "varchar(40)", "varchar(40)");
        const string hostile = "a'b--c;d\0e\"f";

        Convert(c, out var keys, hostile).ShouldBeTrue();

        keys.ShouldBe([hostile]);
    }
}
