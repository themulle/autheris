using System.Security;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// WP-A (GAP-013, GAP-006, GAP-007, GAP-020): bound date forms on SQLite. SEC-PG-01 (closed generator tokens, bound), SEC-PG-02 (no string
/// literal in the emitted SQL), SEC-PG-04 (typed literals), SEC-PG-05 (empty IN list keeps the operand and the row policy).
/// </summary>
[Trait("Category", "SqlGate")]
public sealed class SqliteDatePartParityTests
{
    private static CompileRequest Governed(bool translate = true) =>
        SqliteAstSupportTests.Request(filters: SqliteAstSupportTests.NoRowPolicy()) with
        {
            TranslateTrinoDateFunctions = translate,
            EnforceCatalogProjection = true
        };

    private static CompiledSql Compile(string sql, bool translate = true) =>
        SqliteAstSupportTests.Compile(sql, Governed(translate));

    private static List<object?> Run(CompiledSql compiled, params object?[] noParams)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE \"Orders\" (\"Id\" INTEGER, \"TenantId\" TEXT, \"Region\" TEXT, \"Status\" TEXT, \"Amount\" NUMERIC, \"Email\" TEXT);" +
                "INSERT INTO \"Orders\" VALUES (1,'acme','EU','2024-02-29','1',NULL),(2,'acme','EU','2023-07-15 10:20:30','1',NULL),(3,'other','EU','2024-01-01','1',NULL);";
            create.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        new SqliteCompiledSqlBinder().Bind(cmd, compiled, new Dictionary<string, object?>());
        using var reader = cmd.ExecuteReader();
        var rows = new List<object?>();
        while (reader.Read()) rows.Add(reader.IsDBNull(0) ? null : reader.GetValue(0));
        return rows;
    }

    // ---- GAP-013 extract / date part / trunc / add ----

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("year")]
    [InlineData("quarter")]
    [InlineData("month")]
    [InlineData("day")]
    [InlineData("hour")]
    [InlineData("minute")]
    [InlineData("second")]
    public void Extract_Bound_EmitsNoStringLiteral(string field)
    {
        // SEC-PG-01, SEC-PG-02
        var compiled = Compile($"SELECT extract({field} FROM status) FROM orders");

        Assert.DoesNotContain("'", compiled.Sql);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.QueryLiteral && p.Value is string text && text.StartsWith('%'));
    }

    [Fact]
    public void Extract_Bound_BindsFormatAndEmitsNoLiteral()
    {
        var compiled = Compile("SELECT extract(year FROM status) FROM orders");

        Assert.Contains("strftime(?", compiled.Sql);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.QueryLiteral && Equals(p.Value, "%Y"));
        Assert.Equal(new object?[] { 2024L, 2023L }, Run(compiled));
    }

    [Fact]
    public void YearFunctionAndDatePart_AreTheSameExtract()
    {
        var viaFunction = Compile("SELECT year(status), month(status) FROM orders");
        var viaDatePart = Compile("SELECT date_part('year', status), date_part('month', status) FROM orders");
        var viaExtract = Compile("SELECT extract(year FROM status), extract(month FROM status) FROM orders");

        Assert.Equal(viaExtract.Sql, viaFunction.Sql);
        Assert.Equal(viaExtract.Sql, viaDatePart.Sql);
    }

    [Fact]
    public void YearFunction_WithoutTranslation_IsRejected()
    {
        Assert.ThrowsAny<Exception>(() => Compile("SELECT year(status) FROM orders", translate: false));
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("SELECT date_part(status, status) FROM orders")]
    [InlineData("SELECT date_part('century; drop', status) FROM orders")]
    [InlineData("SELECT date_part('year') FROM orders")]
    public void DatePart_NonLiteralOrUnknownField_IsRejected(string sql)
    {
        // SEC-PG-01: a non-literal or non-closed field is never concatenated.
        Assert.ThrowsAny<Exception>(() => Compile(sql));
    }

    [Fact]
    public void DateTrunc_Bound_Year_BindsModifier()
    {
        var compiled = Compile("SELECT date_trunc('year', status) FROM orders");

        Assert.DoesNotContain("'", compiled.Sql);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "start of year"));
        Assert.Equal(new object?[] { "2024-01-01 00:00:00", "2023-01-01 00:00:00" }, Run(compiled));
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("week")]
    [InlineData("decade")]
    [InlineData("day', 'start of year")]
    public void DateTrunc_UnknownUnit_Rejected(string unit)
    {
        // SEC-PG-01
        Assert.ThrowsAny<Exception>(() => Compile($"SELECT date_trunc('{unit.Replace("'", "''")}', status) FROM orders"));
    }

    [Fact]
    public void DateAdd_Bound_BindsModifier()
    {
        var compiled = Compile("SELECT date_add('day', 1, status) FROM orders");

        Assert.Equal(new object?[] { "2024-03-01 00:00:00", "2023-07-16 10:20:30" }, Run(compiled));
    }

    [Trait("Category", "Security")]
    [Fact]
    public void DateAdd_Bound_AmountIsBoundNumeric()
    {
        // SEC-PG-01, SEC-PG-02: the amount is a bound number, the unit a bound token, nothing is inlined.
        var compiled = Compile("SELECT date_add('week', -3, status) FROM orders");

        Assert.DoesNotContain("'", compiled.Sql);
        Assert.DoesNotContain("21", compiled.Sql);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.QueryLiteral && p.Value is int or long && Convert.ToInt64(p.Value) == -21);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, " days"));
        Assert.Equal(new object?[] { "2024-02-08 00:00:00", "2023-06-24 10:20:30" }, Run(compiled));
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("SELECT date_add('day', status, status) FROM orders")]
    [InlineData("SELECT date_add('day', 1.5, status) FROM orders")]
    [InlineData("SELECT date_add(status, 1, status) FROM orders")]
    public void DateAdd_NonLiteralOrNonIntegerArgument_IsRejected(string sql)
    {
        Assert.ThrowsAny<Exception>(() => Compile(sql));
    }

    [Trait("Category", "Security")]
    [Fact]
    public void LongChainOfDateFunctions_StaysInsideTheParameterCap()
    {
        // SEC-PG-02: every function adds bound parameters; the existing cap rejects an excess instead of emitting a literal.
        string expr = "status";
        for (int i = 0; i < 40000; i++) expr = $"date_add('day', 1, {expr})";
        Assert.ThrowsAny<Exception>(() => Compile($"SELECT {expr} FROM orders"));
    }

    // ---- GAP-006 typed literals ----

    [Fact]
    public void TypedDateLiteral_Bound_IsStringParameter()
    {
        var compiled = Compile("SELECT id FROM orders WHERE status >= DATE '2024-02-29'");

        Assert.DoesNotContain("'", compiled.Sql);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.QueryLiteral && Equals(p.Value, "2024-02-29"));
        Assert.Equal(new object?[] { 1L }, Run(compiled));
    }

    [Fact]
    public void TypedTimestampLiteral_Bound_IsCanonicalised()
    {
        var compiled = Compile("SELECT id FROM orders WHERE status > TIMESTAMP '2023-07-15T10:20:00'");

        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "2023-07-15 10:20:00"));
        Assert.Equal(new object?[] { 1L, 2L }, Run(compiled));
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("DATE '2024-13-45'")]
    [InlineData("DATE '2024-02-29x'")]
    [InlineData("TIMESTAMP '2024-02-29 25:61:61'")]
    public void TypedDateLiteral_Invalid_RejectedWithoutEcho(string literal)
    {
        // SEC-PG-04
        var ex = Assert.ThrowsAny<Exception>(() => Compile($"SELECT id FROM orders WHERE status = {literal}"));

        string submitted = literal[(literal.IndexOf('\'') + 1)..literal.LastIndexOf('\'')];
        Assert.DoesNotContain(submitted, ex.Message, StringComparison.Ordinal);
    }

    // ---- GAP-007 CAST AS DATE ----

    [Theory]
    [InlineData("DATE", "date(")]
    [InlineData("TIMESTAMP", "datetime(")]
    [InlineData("TIME", "time(")]
    public void CastAsDate_EmitsDateFunction(string type, string expected)
    {
        var compiled = Compile($"SELECT cast(status AS {type}) FROM orders");

        Assert.Contains(expected, compiled.Sql);
        Assert.DoesNotContain($"AS {type}", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CastAsDate_KeepsTheIsoText()
    {
        var compiled = Compile("SELECT cast(status AS DATE) FROM orders");

        Assert.Equal(new object?[] { "2024-02-29", "2023-07-15" }, Run(compiled));
    }

    [Fact]
    public void TryCastAsDate_StaysUnsupported()
    {
        Assert.ThrowsAny<Exception>(() => Compile("SELECT try_cast(status AS DATE) FROM orders"));
    }

    [Fact]
    public void CastAsInteger_IsUnchanged()
    {
        var compiled = Compile("SELECT cast(amount AS BIGINT) FROM orders");

        Assert.Contains("CAST(", compiled.Sql);
    }

    // ---- GAP-020 empty IN list ----

    [Fact]
    public void EmptyInList_IsFalse_EmptyNotInList_IsTrue()
    {
        Assert.Empty(Run(Compile("SELECT id FROM orders WHERE id IN ()")));
        Assert.Equal(new object?[] { 1L, 2L }, Run(Compile("SELECT id FROM orders WHERE id NOT IN ()")));
        Assert.Equal(new object?[] { 1L, 2L }, Run(Compile("SELECT id FROM orders WHERE NOT (id IN ())")));
        Assert.Empty(Run(Compile("SELECT id FROM orders WHERE NOT (id NOT IN ())")));
    }

    [Fact]
    public void EmptyInList_NullOperand_IsFalse()
    {
        Assert.Empty(Run(Compile("SELECT id FROM orders WHERE email IN ()")));
        Assert.Equal(new object?[] { 1L, 2L }, Run(Compile("SELECT id FROM orders WHERE email NOT IN ()")));
    }

    [Trait("Category", "Security")]
    [Fact]
    public void EmptyInList_StillVisitsOperand_DeniedColumnRejected()
    {
        // SEC-PG-05: the operand is not folded away. A function outside the allowlist as the operand is still rejected, and an unknown
        // column stays in the emitted SQL (the provider error is the #312 denied answer), so IN () is no existence oracle.
        Assert.ThrowsAny<Exception>(() => Compile("SELECT id FROM orders WHERE load_extension('x') IN ()"));
        Assert.ThrowsAny<Exception>(() => Compile("SELECT id FROM orders WHERE load_extension('x') NOT IN ()"));
        Assert.Contains("\"email\"", Compile("SELECT id FROM orders WHERE email IN ()").Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret_column", Compile("SELECT id FROM orders WHERE secret_column IN ()").Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void EmptyInList_DoesNotRemoveRowPolicy()
    {
        // SEC-PG-05: the tenant predicate is AND-ed after the folded predicate; NOT IN () cannot widen the result past the tenant.
        var compiled = Compile("SELECT id FROM orders WHERE id NOT IN () OR id IN ()");

        Assert.Contains("CAST(\"TenantId\" AS BLOB)", compiled.Sql);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant && (string?)p.Value == "acme");
        Assert.Equal(new object?[] { 1L, 2L }, Run(compiled));
    }

    [Fact]
    public void ClientListParameter_InList_IsUnchanged()
    {
        var compiled = Compile("SELECT id FROM orders WHERE id IN (1, 2)");

        Assert.Contains(" IN (", compiled.Sql);
    }
}
