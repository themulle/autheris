using System.Data.Common;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// #321 (SEC-RG-03): a provider set-operation column-count error answers SQL_SYNTAX_ERROR with the fixed message, classified by
/// provider code, never by client-influenced text. Anything else stays the generic provider error.
/// </summary>
[Trait("Category", "SqlGate")]
public class DmlErrorSanitizerSetOperationTests
{
    private sealed class NumberedException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }

    private sealed class SqlStateException(string sqlState, string message, string? routine = null) : DbException(message)
    {
        public override string? SqlState { get; } = sqlState;
        public string? Routine { get; } = routine;
    }

    private sealed class SqliteLikeException(int code, string message) : DbException(message)
    {
        public int SqliteErrorCode { get; } = code;
    }

    private sealed class PlainException(string message) : DbException(message);

    private const string Leaky = "VICTIM_SECRET_COLUMN";

    private static void AssertSyntax(GovernedSqlException mapped, TargetSqlDialect dialect)
    {
        Assert.Equal(GovernedSqlErrorCodes.SyntaxError, mapped.Code);
        Assert.Equal(dialect, mapped.Dialect);
        Assert.Equal(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.SyntaxError), mapped.Message);
        Assert.DoesNotContain("VICTIM", mapped.Message, StringComparison.Ordinal);
        Assert.Null(mapped.InnerException);
    }

    [Fact]
    public void SqliteColumnCount_IsSyntaxError() =>
        AssertSyntax(DmlErrorSanitizer.Map(TargetSqlDialect.Sqlite, new SqliteLikeException(1,
            "SELECTs to the left and right of UNION do not have the same number of result columns")), TargetSqlDialect.Sqlite);

    [Fact]
    public void PostgreSqlSyntaxState_IsSyntaxError() =>
        AssertSyntax(DmlErrorSanitizer.Map(TargetSqlDialect.PostgreSql, new SqlStateException("42601",
            "each UNION query must have the same number of columns " + Leaky, "transformSetOperationTree")), TargetSqlDialect.PostgreSql);

    [Fact]
    public void SqlServer205_IsSyntaxError() =>
        AssertSyntax(DmlErrorSanitizer.Map(TargetSqlDialect.SqlServer, new NumberedException(205, "All queries combined " + Leaky)), TargetSqlDialect.SqlServer);

    [Fact]
    public void Oracle1789_IsSyntaxError() =>
        AssertSyntax(DmlErrorSanitizer.Map(TargetSqlDialect.Oracle, new NumberedException(1789, "ORA-01789: query block has incorrect number of result columns " + Leaky)), TargetSqlDialect.Oracle);

    [Fact]
    public void DuckDbSetOperation_IsSyntaxError() =>
        AssertSyntax(DmlErrorSanitizer.Map(TargetSqlDialect.DuckDb, new PlainException(
            "Binder Error: Set operations can only apply to expressions with the same number of result columns " + Leaky)), TargetSqlDialect.DuckDb);

    [Fact]
    public void DatabricksNumColumnsMismatch_IsSyntaxError() =>
        AssertSyntax(DmlErrorSanitizer.Map(TargetSqlDialect.Databricks, new PlainException("[NUM_COLUMNS_MISMATCH] UNION " + Leaky)), TargetSqlDialect.Databricks);

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_RG_03_MessageTextAlone_DoesNotClassify()
    {
        var sqlite = DmlErrorSanitizer.Map(TargetSqlDialect.Sqlite, new SqliteLikeException(19,
            "SELECTs to the left and right of UNION do not have the same number of result columns"));
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, sqlite.Code);
        var pg = DmlErrorSanitizer.Map(TargetSqlDialect.PostgreSql, new SqlStateException("42P01", "each UNION query must have the same number of columns"));
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, pg.Code);
        var otherRoutine = DmlErrorSanitizer.Map(TargetSqlDialect.PostgreSql, new SqlStateException("42601", "syntax error at or near", "scanner_yyerror"));
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, otherRoutine.Code);
        var noRoutine = DmlErrorSanitizer.Map(TargetSqlDialect.PostgreSql, new SqlStateException("42601", "x"));
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, noRoutine.Code);
        var sqlServer = DmlErrorSanitizer.Map(TargetSqlDialect.SqlServer, new NumberedException(208, "All queries combined"));
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, sqlServer.Code);
    }
}
