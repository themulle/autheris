using System.Collections.Frozen;
using System.Security;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>WP-X0h: typed column masks rendered by the SQLite generator and executed on in-process SQLite.</summary>
[Trait("Category", "SqlGate")]
public class SqliteMaskTests
{
    private static ColumnReference Col(string name) => new(new SqlQualifiedName(new[] { new SqlIdentifier(name, true) }));

    private static PolicyParameterExpression P(string name, SqlParameterType type) => new(name, type, ParameterOrigin.Mask);

    private static CompiledSql Generate(MaskExpression mask, Dictionary<string, PolicyValue>? values = null)
    {
        var stmt = new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(mask, new SqlIdentifier(mask.Column.Name.SimpleName, true)) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("t", true) }), null),
                null, null, null), null, null);
        return CompilerTestHelpers.Generate(TargetSqlDialect.Sqlite, stmt, values);
    }

    private static object? Run(CompiledSql compiled, string? value)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.CreateFunction("gateway_hmac_sha256", (string? val, string? salt) =>
            val is null ? null : "hmac_" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(salt ?? string.Empty), System.Text.Encoding.UTF8.GetBytes(val))), isDeterministic: true);
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t (Email TEXT, Lat REAL, Status TEXT, Amount NUMERIC)";
            create.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO t (Email, Lat, Status, Amount) VALUES ($v, 48.8566123, 'open', 12.5)";
            insert.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        new SqliteCompiledSqlBinder().Bind(cmd, compiled, new Dictionary<string, object?>());
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static MaskExpression Partial(string maskChar = "*") => new(MaskKind.PartialMask, Col("Email"), new MaskArguments(
        KeepPrefix: P("m_p", SqlParameterType.Int32), KeepSuffix: P("m_s", SqlParameterType.Int32), MaskChar: P("m_c", SqlParameterType.String)), "TEXT");

    private static Dictionary<string, PolicyValue> PartialValues(int prefix, int suffix, string maskChar = "*") => new()
    {
        ["m_p"] = new(prefix, SqlParameterType.Int32), ["m_s"] = new(suffix, SqlParameterType.Int32), ["m_c"] = new(maskChar, SqlParameterType.String)
    };

    [Fact]
    public void Nullify_IsATypedNull_FromTheDeclaredAffinity()
    {
        var c = Generate(new MaskExpression(MaskKind.Nullify, Col("Email"), new MaskArguments(), "VARCHAR(200)"));
        Assert.Contains("CAST(NULL AS TEXT)", c.Sql);
        Assert.Null(Run(c, "x@y.z"));
    }

    [Fact]
    public void Redact_Text_BindsTheReplacement_AndNumericIsNull()
    {
        var text = Generate(new MaskExpression(MaskKind.Redact, Col("Email"), new MaskArguments(Constant: P("m_r", SqlParameterType.String)), "TEXT"),
            new() { ["m_r"] = new("[REDACTED]", SqlParameterType.String) });
        Assert.DoesNotContain("REDACTED", text.Sql);
        Assert.Equal("[REDACTED]", Run(text, "x@y.z"));

        var numeric = Generate(new MaskExpression(MaskKind.Redact, Col("Amount"), new MaskArguments(), "NUMERIC"));
        Assert.Contains("CAST(NULL AS NUMERIC)", numeric.Sql);
        Assert.Null(Run(numeric, "x"));
    }

    [Fact]
    public void Constant_IsBound_AndCastToTheAffinity()
    {
        var c = Generate(new MaskExpression(MaskKind.Constant, Col("Status"), new MaskArguments(Constant: P("m_k", SqlParameterType.String)), "TEXT"),
            new() { ["m_k"] = new("hidden", SqlParameterType.String) });
        Assert.DoesNotContain("hidden", c.Sql);
        Assert.Equal("hidden", Run(c, "x"));
    }

    [Fact]
    public void PartialMask_KeepsPrefixAndSuffix_AndMasksTheRest()
    {
        var c = Generate(Partial(), PartialValues(2, 3));
        Assert.DoesNotContain("'*'", c.Sql);
        Assert.Equal("al" + new string('*', 24 - 5) + "ple", Run(c, "alice.smith@acme.example"));
    }

    [Fact]
    public void PartialMask_ZeroSuffix_DoesNotExposeTheWholeValue()
    {
        var c = Generate(Partial(), PartialValues(1, 0));
        Assert.Equal("a" + new string('*', 8), Run(c, "abcdefghi"));
    }

    [Fact]
    public void PartialMask_ShortValue_IsFullyMaskedWithFiveCharacters()
    {
        var c = Generate(Partial(), PartialValues(2, 3));
        Assert.Equal("*****", Run(c, "abcde"));
    }

    [Fact]
    public void PartialMask_NegativeCounts_AreClampedToZero()
    {
        var c = Generate(Partial(), PartialValues(-4, -2));
        Assert.Equal("********", Run(c, "abcdefgh"));
    }

    [Fact]
    public void PartialMask_NullStaysNull()
    {
        Assert.Null(Run(Generate(Partial(), PartialValues(2, 3)), null));
    }

    [Fact]
    public void Hmac_UsesTheGatewayUdf_WithABoundKey()
    {
        var c = Generate(new MaskExpression(MaskKind.Hmac, Col("Email"), new MaskArguments(HmacKey: P("m_k", SqlParameterType.String)), "TEXT"),
            new() { ["m_k"] = new("00ff", SqlParameterType.String) });

        Assert.Contains("gateway_hmac_sha256(CAST(\"Email\" AS TEXT), ?1)", c.Sql);
        Assert.DoesNotContain("00ff", c.Sql);
        Assert.Equal("00ff", Assert.Single(c.Parameters).Value);
        string expected = "hmac_" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("00ff"), System.Text.Encoding.UTF8.GetBytes("a@b.c")));
        Assert.Equal(expected, Run(c, "a@b.c"));
    }

    [Fact]
    public void GeoJitter_RoundsWithAStructuralScale_Clamped()
    {
        var c = Generate(new MaskExpression(MaskKind.GeoJitter, Col("Lat"), new MaskArguments(Decimals: 9), "REAL"));
        Assert.Contains("ROUND(\"Lat\", 6)", c.Sql);
        Assert.Equal(48.856612, Convert.ToDouble(Run(c, "x")), 6);
    }

    [Fact]
    public void MaskArgument_WithoutValue_FailsClosed()
    {
        Assert.Throws<SecurityException>(() => Generate(
            new MaskExpression(MaskKind.Constant, Col("Status"), new MaskArguments(Constant: P("missing", SqlParameterType.String)), "TEXT")));
    }

    [Theory]
    [InlineData("TEXT); DROP TABLE x; --")]
    [InlineData("VARCHAR(200")]
    [InlineData("")]
    public void ADeclaredTypeWithForeignCharacters_IsRejected(string dataType)
    {
        Assert.Throws<SecurityException>(() => Generate(new MaskExpression(MaskKind.Nullify, Col("Email"), new MaskArguments(), dataType)));
    }
}
