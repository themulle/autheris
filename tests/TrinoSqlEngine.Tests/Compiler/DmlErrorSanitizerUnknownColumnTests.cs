using System.Data.Common;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// #312 (SEC-PG-10): a provider "unknown identifier" error of the governed executor answers like a denied column (code
/// <c>SQL_POLICY_DENIED</c>, fixed message), classified by provider code, never by client-influenced text. Anything else stays the
/// generic provider error.
/// </summary>
[Trait("Category", "SqlGate")]
public class DmlErrorSanitizerUnknownColumnTests
{
    private sealed class NumberedException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }

    private sealed class SqlStateException(string sqlState, string message) : DbException(message)
    {
        public override string? SqlState { get; } = sqlState;
    }

    private sealed class SqliteLikeException(int code, string message) : DbException(message)
    {
        public int SqliteErrorCode { get; } = code;
    }

    private sealed class PlainException(string message) : DbException(message);

    private const string Leaky = "no such column: VICTIM_SECRET_COLUMN";

    private static void AssertDenied(GovernedSqlException mapped, TargetSqlDialect dialect)
    {
        Assert.Equal(GovernedSqlErrorCodes.PolicyDenied, mapped.Code);
        Assert.Equal(dialect, mapped.Dialect);
        Assert.Equal(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.PolicyDenied), mapped.Message);
        Assert.DoesNotContain("VICTIM", mapped.Message, StringComparison.Ordinal);
        Assert.Null(mapped.InnerException);
    }

    [Fact]
    public void SqliteNoSuchColumn_IsPolicyDenied() =>
        AssertDenied(DmlErrorSanitizer.Map(TargetSqlDialect.Sqlite, new SqliteLikeException(1, Leaky)), TargetSqlDialect.Sqlite);

    [Fact]
    public void PostgreSqlUndefinedColumn_IsPolicyDenied() =>
        AssertDenied(DmlErrorSanitizer.Map(TargetSqlDialect.PostgreSql, new SqlStateException("42703", "column \"VICTIM\" does not exist")), TargetSqlDialect.PostgreSql);

    [Fact]
    public void SqlServerInvalidColumnName_IsPolicyDenied() =>
        AssertDenied(DmlErrorSanitizer.Map(TargetSqlDialect.SqlServer, new NumberedException(207, "Invalid column name 'VICTIM'.")), TargetSqlDialect.SqlServer);

    [Fact]
    public void OracleInvalidIdentifier_IsPolicyDenied() =>
        AssertDenied(DmlErrorSanitizer.Map(TargetSqlDialect.Oracle, new NumberedException(904, "ORA-00904: \"VICTIM\": invalid identifier")), TargetSqlDialect.Oracle);

    [Fact]
    public void DuckDbBinderError_IsPolicyDenied() =>
        AssertDenied(DmlErrorSanitizer.Map(TargetSqlDialect.DuckDb, new PlainException("Binder Error: Referenced column \"VICTIM\" not found in FROM clause!")), TargetSqlDialect.DuckDb);

    [Fact]
    public void WrappedUnknownColumn_IsPolicyDenied() =>
        AssertDenied(DmlErrorSanitizer.Map(TargetSqlDialect.Sqlite,
            new InvalidOperationException("wrapper", new SqliteLikeException(1, Leaky))), TargetSqlDialect.Sqlite);

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_PG_10_MessageTextAlone_DoesNotClassify()
    {
        // Another error code with the same words is not an unknown column: classification is by provider code first.
        var sqlite = DmlErrorSanitizer.Map(TargetSqlDialect.Sqlite, new SqliteLikeException(19, Leaky));
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, sqlite.Code);
        var pg = DmlErrorSanitizer.Map(TargetSqlDialect.PostgreSql, new SqlStateException("42P01", "column \"x\" does not exist"));
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, pg.Code);
        var sqlServer = DmlErrorSanitizer.Map(TargetSqlDialect.SqlServer, new NumberedException(208, "Invalid column name 'x'."));
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, sqlServer.Code);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_PG_10_UnrecognisedProviderError_StaysProviderError()
    {
        var mapped = DmlErrorSanitizer.Map(TargetSqlDialect.Sqlite, new SqliteLikeException(1, "near \"FROM\": syntax error VICTIM"));
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, mapped.Code);
        Assert.DoesNotContain("VICTIM", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_PG_10_UnknownColumn_IsByteIdenticalToTheDeniedAnswer()
    {
        var unknown = DmlErrorSanitizer.Map(TargetSqlDialect.Sqlite, new SqliteLikeException(1, Leaky));
        var denied = new GovernedSqlException(GovernedSqlErrorCodes.PolicyDenied, TargetSqlDialect.Sqlite);
        Assert.Equal(denied.Code, unknown.Code);
        Assert.Equal(denied.Message, unknown.Message);
    }
}
