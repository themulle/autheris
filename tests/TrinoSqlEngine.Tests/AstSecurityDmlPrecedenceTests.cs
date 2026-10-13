namespace TrinoSqlEngine.Tests;

using System;
using System.Text.RegularExpressions;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Tests.Security;
using Xunit;

/// <summary>
/// A user condition in a DML WHERE clause is grouped, so that it cannot absorb the injected tenant predicate by operator
/// precedence (OR below AND), and composite operands keep their parentheses. Asserted on the compiled text of the AST path.
/// </summary>
public sealed class AstSecurityDmlPrecedenceTests
{
    private static CompiledSql Compile(TargetSqlDialect dialect, string sql) => new ProbeEnv(dialect).Compile(sql);

    private static void TenantIsBound(CompiledSql compiled) =>
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);

    private static string Flatten(string sql) => Regex.Replace(sql, @"\s+", " ");

    // the user's own spelling is emitted for user references (lower case here); match any quoting style, ignoring case
    private const string Id = @"(?:""id""|\[id\]|id)";
    private const string Amount = @"(?:""amount""|\[amount\]|amount)";
    private const string Status = @"(?:""status""|\[status\]|status)";

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void Update_With_User_Or_In_Operand_Emits_Parentheses_Preserving_Rls(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, "UPDATE orders SET status = 'cancelled' WHERE (id = 1 OR amount = 2) IN (TRUE)");

        Assert.Matches(@"(?i)" + @"\(" + Id + @"\s*=\s*\S+\s+OR\s+" + Amount + @"\s*=\s*\S+\)\s+IN", Flatten(compiled.Sql));
        TenantIsBound(compiled);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void Delete_With_User_Or_Condition_Does_Not_Bypass_Tenant_Rls(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, "DELETE FROM orders WHERE amount > 10 OR status = 'pending'");

        // The user condition is grouped: (amount > ? OR status = ?) AND (tenant predicate)
        Assert.Matches(@"(?i)" + @"\(" + Amount + @"\s*>\s*\S+\s+OR\s+" + Status + @"\s*=\s*\S+\)\s+AND", Flatten(compiled.Sql));
        TenantIsBound(compiled);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void Delete_With_Composite_InSubquery_Operand_Emits_Parentheses(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, "DELETE FROM orders WHERE (id + amount) IN (SELECT id FROM entitlements)");

        Assert.Matches(@"(?i)" + @"\(" + Id + @"\s*\+\s*" + Amount + @"\)\s+IN", Flatten(compiled.Sql));
        TenantIsBound(compiled);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void Update_With_Composite_Between_Operand_Emits_Parentheses(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, "UPDATE orders SET status = 'active' WHERE (id + amount) BETWEEN 1 AND 10");

        Assert.Matches(@"(?i)" + @"\(" + Id + @"\s*\+\s*" + Amount + @"\)\s+BETWEEN", Flatten(compiled.Sql));
        TenantIsBound(compiled);
    }

    [Fact]
    public void Delete_With_IsDistinctFrom_In_Sqlite_Emits_Parentheses_For_Composite_Operands()
    {
        var compiled = Compile(TargetSqlDialect.Sqlite, "DELETE FROM orders WHERE (id = 1 OR amount = 2) IS DISTINCT FROM FALSE");

        // SQLite formats IS DISTINCT FROM as IS NOT
        Assert.Matches(@"(?i)" + @"\(" + Id + @"\s*=\s*\S+\s+OR\s+" + Amount + @"\s*=\s*\S+\)\s+IS\s+NOT", Flatten(compiled.Sql));
        TenantIsBound(compiled);
    }
}
