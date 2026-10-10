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

    // CR-ADG-34: 547 covers FOREIGN KEY, REFERENCE and CHECK; the message class (read, never echoed) splits them.
    [Theory]
    [InlineData("The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_Orders_Cust\". The conflict occurred in database \"db\", table \"dbo.Customers\", column 'Id'.", DmlConstraintKind.ForeignKey)]
    [InlineData("The DELETE statement conflicted with the REFERENCE constraint \"FK_Orders_Cust\". The conflict occurred in database \"db\", table \"dbo.Orders\", column 'CustId'.", DmlConstraintKind.ForeignKey)]
    [InlineData("The UPDATE statement conflicted with the CHECK constraint \"CK_Orders_Amount\". The conflict occurred in database \"db\", table \"dbo.Orders\", column 'Amount'.", DmlConstraintKind.Check)]
    public void SqlServer547_IsSplitIntoForeignKeyAndCheck(string message, DmlConstraintKind kind)
    {
        var mapped = DmlErrorSanitizer.TryMap(TargetSqlDialect.SqlServer, new NumberedException(547, message));
        Assert.NotNull(mapped);
        Assert.Equal(kind, mapped.Kind);
        AssertNoLeak(mapped);
        Assert.DoesNotContain("CK_", mapped.ToString());
        Assert.DoesNotContain("FK_", mapped.ToString());
    }

    [Fact]
    public void SqlServer547_WithAnUnknownMessageClass_IsNotGuessed_ButStillMapsToAGenericError()
    {
        var error = new NumberedException(547, "Msg in another language VICTIM-KEY-42 dbo.Orders");
        Assert.Null(DmlErrorSanitizer.TryMap(TargetSqlDialect.SqlServer, error));
        AssertGeneric(DmlErrorSanitizer.Map(TargetSqlDialect.SqlServer, error), GovernedSqlErrorCodes.ProviderError);
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

    // ---- CR-ADG-34: every provider error on the governed path maps to a generic typed error ----

    private const string Value = "VICTIM-VALUE-77";
    private const string Column = "SecretColumn";
    private const string Table = "SecretTable";

    private static string Leak(string prefix) => $"{prefix} value '{Value}' column '{Column}' table 'dbo.{Table}' inner-detail";

    private static void AssertGeneric(GovernedSqlException error, string code)
    {
        Assert.Equal(code, error.Code);
        Assert.Null(error.InnerException);
        Assert.Equal(GovernedSqlErrorCodes.FixedMessage(code), error.Message);
        string text = error.ToString();
        Assert.DoesNotContain(Value, text);
        Assert.DoesNotContain(Column, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Table, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inner-detail", text);
    }

    public static IEnumerable<object[]> DataErrors() => new[]
    {
        new object[] { TargetSqlDialect.SqlServer, new NumberedException(2628, Leak("String or binary data would be truncated in table")) },
        new object[] { TargetSqlDialect.SqlServer, new NumberedException(245, Leak("Conversion failed when converting the varchar")) },
        new object[] { TargetSqlDialect.SqlServer, new NumberedException(8114, Leak("Error converting data type")) },
        new object[] { TargetSqlDialect.Oracle, new NumberedException(12899, Leak("ORA-12899: value too large for column")) },
        new object[] { TargetSqlDialect.Oracle, new NumberedException(1722, Leak("ORA-01722: invalid number")) },
        new object[] { TargetSqlDialect.PostgreSql, new SqlStateException("22P02", Leak("invalid input syntax for type integer")) },
        new object[] { TargetSqlDialect.PostgreSql, new SqlStateException("22001", Leak("value too long for type")) },
        new object[] { TargetSqlDialect.DuckDb, new InvalidOperationException(Leak("Conversion Error: Could not convert string")) },
        new object[] { TargetSqlDialect.Databricks, new InvalidOperationException(Leak("[CAST_INVALID_INPUT] The value cannot be cast")) }
    };

    [Theory]
    [MemberData(nameof(DataErrors))]
    public void DataErrors_MapToAFixedDataError_WithoutValueColumnOrTableName(TargetSqlDialect dialect, Exception error)
    {
        Assert.Null(DmlErrorSanitizer.TryMap(dialect, error));
        var mapped = DmlErrorSanitizer.Map(dialect, error);
        AssertGeneric(mapped, GovernedSqlErrorCodes.DataError);
        Assert.Equal(dialect, mapped.Dialect);
    }

    public static IEnumerable<object[]> UnclassifiedErrors() => new[]
    {
        new object[] { TargetSqlDialect.SqlServer, new NumberedException(918273, Leak("Msg")) },
        new object[] { TargetSqlDialect.Oracle, new NumberedException(918, Leak("ORA-00918: column ambiguously defined")) },
        new object[] { TargetSqlDialect.PostgreSql, new SqlStateException("42712", Leak("table name specified more than once")) },
        new object[] { TargetSqlDialect.DuckDb, new InvalidOperationException(Leak("Binder Error: Ambiguous reference")) },
        new object[] { TargetSqlDialect.Databricks, new InvalidOperationException(Leak("[AMBIGUOUS_REFERENCE] Reference is ambiguous")) },
        new object[] { TargetSqlDialect.SqlServer, new InvalidOperationException("wrapper", new NumberedException(918273, Leak("Msg"))) }
    };

    [Theory]
    [MemberData(nameof(UnclassifiedErrors))]
    public void EveryOtherProviderError_MapsToTheGenericProviderError(TargetSqlDialect dialect, Exception error)
    {
        var mapped = DmlErrorSanitizer.Map(dialect, error);
        AssertGeneric(mapped, GovernedSqlErrorCodes.ProviderError);
        Assert.Equal(dialect, mapped.Dialect);
    }

    [Theory]
    [MemberData(nameof(ConstraintErrors))]
    public void ConstraintErrors_KeepTheirCategory_AndTheStableCode(TargetSqlDialect dialect, Exception error, DmlConstraintKind kind)
    {
        var mapped = DmlErrorSanitizer.Map(dialect, error);
        var constraint = Assert.IsType<DmlConstraintViolationException>(mapped);
        Assert.Equal(kind, constraint.Kind);
        AssertGeneric(mapped, GovernedSqlErrorCodes.ConstraintViolation);
    }

    public static IEnumerable<object[]> ConstraintErrors() => new[]
    {
        new object[] { TargetSqlDialect.SqlServer, new NumberedException(2627, Leak("Violation of PRIMARY KEY")), DmlConstraintKind.Unique },
        new object[] { TargetSqlDialect.Oracle, new NumberedException(1, Leak("ORA-00001")), DmlConstraintKind.Unique },
        new object[] { TargetSqlDialect.PostgreSql, new SqlStateException("23505", Leak("duplicate key")), DmlConstraintKind.Unique },
        new object[] { TargetSqlDialect.DuckDb, new InvalidOperationException(Leak("Constraint Error: Duplicate key")), DmlConstraintKind.Unique },
        new object[] { TargetSqlDialect.Databricks, new InvalidOperationException(Leak("[DELTA_NOT_NULL_CONSTRAINT_VIOLATED]")), DmlConstraintKind.NotNull }
    };

    [Fact]
    public void TheCodesAreStable_AndTheMessagesAreFixed()
    {
        Assert.Equal("DML_CONSTRAINT_VIOLATION", GovernedSqlErrorCodes.ConstraintViolation);
        Assert.Equal("SQL_DATA_ERROR", GovernedSqlErrorCodes.DataError);
        Assert.Equal("SQL_PROVIDER_ERROR", GovernedSqlErrorCodes.ProviderError);
        Assert.Equal("DML_CHECK_OPTION_VIOLATION", GovernedSqlErrorCodes.CheckOptionViolation);
        Assert.All(new[] { GovernedSqlErrorCodes.ConstraintViolation, GovernedSqlErrorCodes.DataError, GovernedSqlErrorCodes.ProviderError, GovernedSqlErrorCodes.CheckOptionViolation },
            code => Assert.False(string.IsNullOrWhiteSpace(GovernedSqlErrorCodes.FixedMessage(code))));
        Assert.Equal(GovernedSqlErrorCodes.ConstraintViolation,
            new DmlConstraintViolationException(DmlConstraintKind.Unique, TargetSqlDialect.SqlServer).Code);
    }

    [Fact]
    public void Map_NeverReturnsNull_AndNeverKeepsTheOriginalOrAnInnerException()
    {
        var mapped = DmlErrorSanitizer.Map(TargetSqlDialect.Oracle, new InvalidOperationException(Leak("anything")));
        Assert.Null(mapped.InnerException);
        Assert.DoesNotContain(Value, mapped.ToString());
        Assert.Throws<ArgumentNullException>(() => DmlErrorSanitizer.Map(TargetSqlDialect.Oracle, null!));
    }
}
