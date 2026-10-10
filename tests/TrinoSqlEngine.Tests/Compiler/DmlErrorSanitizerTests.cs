using System.Data.Common;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>R-12 / SEC-ADG-08 d: constraint violations reach the caller as a typed error without driver text, key value or names.</summary>
public class DmlErrorSanitizerTests
{
    private sealed class NumberedException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }

    private sealed class SqlStateException(string sqlState, string message) : DbException(message)
    {
        public override string? SqlState { get; } = sqlState;
    }

    private const string Leaky = "Violation of PRIMARY KEY constraint 'PK__Orders__3213E83F'. Cannot insert duplicate key in object 'dbo.Orders'. The duplicate key value is (VICTIM-KEY-42).";

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, 2627, DmlConstraintKind.Unique)]
    [InlineData(TargetSqlDialect.SqlServer, 2601, DmlConstraintKind.Unique)]
    [InlineData(TargetSqlDialect.SqlServer, 547, DmlConstraintKind.ForeignKey)]
    [InlineData(TargetSqlDialect.SqlServer, 515, DmlConstraintKind.NotNull)]
    [InlineData(TargetSqlDialect.SqlServer, 8672, DmlConstraintKind.MergeMultipleMatches)]
    [InlineData(TargetSqlDialect.Oracle, 1, DmlConstraintKind.Unique)]
    [InlineData(TargetSqlDialect.Oracle, 1400, DmlConstraintKind.NotNull)]
    [InlineData(TargetSqlDialect.Oracle, 30926, DmlConstraintKind.MergeMultipleMatches)]
    public void NumberedProviderErrors_AreMappedToATypedError_WithoutDriverText(TargetSqlDialect dialect, int number, DmlConstraintKind kind)
    {
        var mapped = DmlErrorSanitizer.TryMap(dialect, new NumberedException(number, Leaky));
        Assert.NotNull(mapped);
        Assert.Equal(kind, mapped.Kind);
        Assert.Equal(dialect, mapped.Dialect);
        AssertNoLeak(mapped);
    }

    [Theory]
    [InlineData("23505", DmlConstraintKind.Unique)]
    [InlineData("23503", DmlConstraintKind.ForeignKey)]
    [InlineData("23502", DmlConstraintKind.NotNull)]
    [InlineData("23514", DmlConstraintKind.Check)]
    [InlineData("21000", DmlConstraintKind.MergeMultipleMatches)]
    public void PostgreSqlStates_AreMapped(string sqlState, DmlConstraintKind kind)
    {
        var mapped = DmlErrorSanitizer.TryMap(TargetSqlDialect.PostgreSql, new SqlStateException(sqlState, "duplicate key value violates unique constraint \"orders_pkey\" Key (id)=(VICTIM-KEY-42) already exists."));
        Assert.NotNull(mapped);
        Assert.Equal(kind, mapped.Kind);
        AssertNoLeak(mapped);
    }

    [Theory]
    [InlineData(TargetSqlDialect.DuckDb, "Constraint Error: Duplicate key \"id: VICTIM-KEY-42\" violates primary key constraint.", DmlConstraintKind.Unique)]
    [InlineData(TargetSqlDialect.DuckDb, "Constraint Error: NOT NULL constraint failed: orders.status", DmlConstraintKind.NotNull)]
    [InlineData(TargetSqlDialect.Databricks, "[DELTA_MULTIPLE_SOURCE_ROW_MATCHING_TARGET_ROW_IN_MERGE] Cannot perform Merge VICTIM-KEY-42", DmlConstraintKind.MergeMultipleMatches)]
    public void MessageClassified_Dialects_AreMapped(TargetSqlDialect dialect, string message, DmlConstraintKind kind)
    {
        var mapped = DmlErrorSanitizer.TryMap(dialect, new InvalidOperationException(message));
        Assert.NotNull(mapped);
        Assert.Equal(kind, mapped.Kind);
        AssertNoLeak(mapped);
    }

    [Fact]
    public void AWrappedProviderError_IsFound_AndTheWrapperIsNotKept()
    {
        var wrapped = new InvalidOperationException("command failed", new NumberedException(2627, Leaky));
        var mapped = DmlErrorSanitizer.TryMap(TargetSqlDialect.SqlServer, wrapped);
        Assert.NotNull(mapped);
        Assert.Null(mapped.InnerException);
    }

    [Fact]
    public void OtherErrors_AreNotMapped()
    {
        Assert.Null(DmlErrorSanitizer.TryMap(TargetSqlDialect.SqlServer, new NumberedException(102, "Incorrect syntax")));
        Assert.Null(DmlErrorSanitizer.TryMap(TargetSqlDialect.PostgreSql, new SqlStateException("42601", "syntax error")));
        Assert.Null(DmlErrorSanitizer.TryMap(TargetSqlDialect.Oracle, new InvalidOperationException("x")));
        Assert.Null(DmlErrorSanitizer.TryMap(TargetSqlDialect.Sqlite, new NumberedException(2627, "x")));
    }

    private static void AssertNoLeak(DmlConstraintViolationException error)
    {
        Assert.Equal("The statement violated a data constraint.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("VICTIM", error.ToString());
        Assert.DoesNotContain("Orders", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PK__", error.ToString());
    }
}
