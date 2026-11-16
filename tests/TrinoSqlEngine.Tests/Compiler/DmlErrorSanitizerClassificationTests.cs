using System.Data.Common;
using TrinoSqlEngine.Ast.Emit;
using Xunit;
using static TrinoSqlEngine.TargetSqlDialect;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) pinning every provider error number, SQLSTATE and message token the sanitizer classifies, per dialect and
/// with the neighbouring non-matching values: a constraint category, the recursion limit, an unknown column (answered like a denied
/// one), a set-operation degree error, a data error, or the generic provider error. The caller never sees driver text, so a wrong
/// class would either leak a distinguishing oracle or hide a caller mistake behind a 502.
/// </summary>
[Trait("Category", "SqlGate")]
public class DmlErrorSanitizerClassificationTests
{
    private sealed class MessageEx : Exception
    {
        public MessageEx(string message) : base(message)
        {
        }

        public MessageEx(string message, Exception inner) : base(message, inner)
        {
        }
    }

    private sealed class NumEx(int number, string message = "m") : Exception(message)
    {
        public int Number { get; } = number;
    }

    private sealed class NumberItem(int number)
    {
        public int Number { get; } = number;
    }

    private sealed class ErrorsEx(params int[] numbers) : Exception("m")
    {
        public IReadOnlyList<NumberItem> Errors { get; } = numbers.Select(n => new NumberItem(n)).ToArray();
    }

    private sealed class StateEx(string state, string? routine = null) : Exception("m")
    {
        public string SqlState { get; } = state;
        public string? Routine { get; } = routine;
    }

    private sealed class DbStateEx(string state) : DbException("m")
    {
        public override string? SqlState { get; } = state;
    }

    private sealed class RoutineDbStateEx(string state, string routine) : DbException("m")
    {
        public override string? SqlState { get; } = state;
        public string Routine { get; } = routine;
    }

    private const string Provider = GovernedSqlErrorCodes.ProviderError;
    private const string Data = GovernedSqlErrorCodes.DataError;
    private const string Limit = GovernedSqlErrorCodes.LimitExceeded;
    private const string Denied = GovernedSqlErrorCodes.PolicyDenied;
    private const string Syntax = GovernedSqlErrorCodes.SyntaxError;
    private const string Constraint = GovernedSqlErrorCodes.ConstraintViolation;

    private static object[] Row(TargetSqlDialect dialect, Exception error, string code, DmlConstraintKind? kind = null) => [dialect, error, code, kind!];

    public static IEnumerable<object[]> Cases()
    {
        // SQL Server
        foreach (int n in new[] { 2627, 2601 }) yield return Row(SqlServer, new NumEx(n), Constraint, DmlConstraintKind.Unique);
        yield return Row(SqlServer, new NumEx(547, "The UPDATE statement conflicted with the CHECK constraint"), Constraint, DmlConstraintKind.Check);
        yield return Row(SqlServer, new NumEx(547, "conflicted with the check constraint"), Constraint, DmlConstraintKind.Check);
        yield return Row(SqlServer, new NumEx(547, "conflicted with the FOREIGN KEY constraint"), Constraint, DmlConstraintKind.ForeignKey);
        yield return Row(SqlServer, new NumEx(547, "conflicted with the REFERENCE constraint"), Constraint, DmlConstraintKind.ForeignKey);
        yield return Row(SqlServer, new NumEx(547, "conflicted with something else"), Provider);
        yield return Row(SqlServer, new NumEx(515), Constraint, DmlConstraintKind.NotNull);
        yield return Row(SqlServer, new NumEx(8672), Constraint, DmlConstraintKind.MergeMultipleMatches);
        yield return Row(SqlServer, new NumEx(530), Limit);
        yield return Row(SqlServer, new ErrorsEx(1, 530), Limit);
        yield return Row(SqlServer, new NumEx(207), Denied);
        yield return Row(SqlServer, new ErrorsEx(1, 207), Denied);
        yield return Row(SqlServer, new NumEx(205), Syntax);
        yield return Row(SqlServer, new ErrorsEx(1, 205), Syntax);
        foreach (int n in new[] { 2628, 8152, 245, 8114, 8115, 242, 241, 220, 232, 295, 9803 }) yield return Row(SqlServer, new NumEx(n), Data);
        foreach (int n in new[] { 1, 2, 244, 246, 2629, 8151, 9802, 9804, 531, 206, 208, 516 }) yield return Row(SqlServer, new NumEx(n), Provider);
        yield return Row(SqlServer, new ErrorsEx(1, 2), Provider);
        yield return Row(SqlServer, new ErrorsEx(), Provider);

        // Oracle
        yield return Row(Oracle, new NumEx(1), Constraint, DmlConstraintKind.Unique);
        foreach (int n in new[] { 2291, 2292 }) yield return Row(Oracle, new NumEx(n), Constraint, DmlConstraintKind.ForeignKey);
        foreach (int n in new[] { 1400, 1407 }) yield return Row(Oracle, new NumEx(n), Constraint, DmlConstraintKind.NotNull);
        yield return Row(Oracle, new NumEx(2290), Constraint, DmlConstraintKind.Check);
        yield return Row(Oracle, new NumEx(30926), Constraint, DmlConstraintKind.MergeMultipleMatches);
        yield return Row(Oracle, new NumEx(32044), Limit);
        yield return Row(Oracle, new ErrorsEx(5, 32044), Limit);
        yield return Row(Oracle, new NumEx(904), Denied);
        yield return Row(Oracle, new ErrorsEx(5, 904), Denied);
        yield return Row(Oracle, new NumEx(1789), Syntax);
        yield return Row(Oracle, new ErrorsEx(5, 1789), Syntax);
        foreach (int n in new[] { 12899, 1722, 1858, 1861, 1840, 1843, 1438, 1401, 1476, 6502, 1830 }) yield return Row(Oracle, new NumEx(n), Data);
        foreach (int n in new[] { 2, 530, 207, 205, 2627, 547, 515, 8672, 2293, 1399, 1408, 2289, 30925, 32043, 903, 1788 }) yield return Row(Oracle, new NumEx(n), Provider);

        // PostgreSQL (SQLSTATE through a DbException or through a property)
        foreach (var (state, kind) in new[]
        {
            ("23505", DmlConstraintKind.Unique), ("23503", DmlConstraintKind.ForeignKey), ("23502", DmlConstraintKind.NotNull),
            ("23514", DmlConstraintKind.Check), ("21000", DmlConstraintKind.MergeMultipleMatches)
        })
        {
            yield return Row(PostgreSql, new DbStateEx(state), Constraint, kind);
            yield return Row(PostgreSql, new StateEx(state), Constraint, kind);
        }

        yield return Row(PostgreSql, new DbStateEx("42703"), Denied);
        yield return Row(PostgreSql, new StateEx("42703"), Denied);
        yield return Row(PostgreSql, new StateEx("42601", "transformSetOperationTree"), Syntax);
        yield return Row(PostgreSql, new RoutineDbStateEx("42601", "transformSetOperationTree"), Syntax);
        yield return Row(PostgreSql, new StateEx("42601", "other"), Provider);
        yield return Row(PostgreSql, new StateEx("42601"), Provider);
        yield return Row(PostgreSql, new DbStateEx("42601"), Provider);
        yield return Row(PostgreSql, new StateEx("42602", "transformSetOperationTree"), Provider);
        foreach (string state in new[] { "22001", "22003", "22P02", "22000", "22999" })
        {
            yield return Row(PostgreSql, new DbStateEx(state), Data);
            yield return Row(PostgreSql, new StateEx(state), Data);
        }

        foreach (string state in new[] { "2200", "220000", "23000", "23504", "21001", "42704", "21", "", "42P01", "02000" })
        {
            yield return Row(PostgreSql, new StateEx(state), Provider);
        }

        yield return Row(PostgreSql, new NumEx(2627), Provider);

        // DuckDB (message classes)
        foreach (string m in new[] { "Duplicate key \"id\"", "violates primary key constraint", "Violates Unique Constraint", "DUPLICATE KEY" })
            yield return Row(DuckDb, new MessageEx(m), Constraint, DmlConstraintKind.Unique);
        yield return Row(DuckDb, new MessageEx("NOT NULL constraint failed: t.c"), Constraint, DmlConstraintKind.NotNull);
        yield return Row(DuckDb, new MessageEx("not null constraint failed"), Constraint, DmlConstraintKind.NotNull);
        yield return Row(DuckDb, new MessageEx("Constraint Error: violates foreign key constraint"), Constraint, DmlConstraintKind.ForeignKey);
        yield return Row(DuckDb, new MessageEx("CHECK constraint failed: t"), Constraint, DmlConstraintKind.Check);
        yield return Row(DuckDb, new MessageEx("MERGE INTO matched multiple rows"), Constraint, DmlConstraintKind.MergeMultipleMatches);
        yield return Row(DuckDb, new MessageEx("merge into: multiple"), Constraint, DmlConstraintKind.MergeMultipleMatches);
        yield return Row(DuckDb, new MessageEx("MERGE INTO failed"), Provider);
        yield return Row(DuckDb, new MessageEx("matched multiple rows"), Provider);
        yield return Row(DuckDb, new MessageEx("Binder Error: Referenced column \"x\" not found"), Denied);
        yield return Row(DuckDb, new MessageEx("Binder Error: Table \"t\" does not have a column named \"x\""), Denied);
        yield return Row(DuckDb, new MessageEx("Binder Error: something else"), Provider);
        yield return Row(DuckDb, new MessageEx("Referenced column \"x\" not found"), Provider);
        yield return Row(DuckDb, new MessageEx("does not have a column named"), Provider);
        yield return Row(DuckDb, new MessageEx("binder error: Referenced column"), Provider);
        yield return Row(DuckDb, new MessageEx("Binder Error: UNION: queries must have the same number of result columns"), Syntax);
        yield return Row(DuckDb, new MessageEx("queries must have the same number of result columns"), Provider);
        foreach (string m in new[] { "Conversion Error: bad", "conversion error", "Out of Range Error", "out of range", "Could not convert string", "could not convert" })
            yield return Row(DuckDb, new MessageEx(m), Data);
        yield return Row(DuckDb, new MessageEx("Catalog Error"), Provider);

        // Databricks (error classes, case sensitive)
        yield return Row(Databricks, new MessageEx("[DELTA_MULTIPLE_SOURCE_ROW_MATCHING_TARGET_ROW_IN_MERGE] x"), Constraint, DmlConstraintKind.MergeMultipleMatches);
        yield return Row(Databricks, new MessageEx("[DELTA_NOT_NULL_CONSTRAINT_VIOLATED] x"), Constraint, DmlConstraintKind.NotNull);
        yield return Row(Databricks, new MessageEx("[DELTA_NON_NULLABLE_COLUMN_OMITTED] x"), Constraint, DmlConstraintKind.NotNull);
        yield return Row(Databricks, new MessageEx("[DELTA_VIOLATE_CONSTRAINT_WITH_VALUES] x"), Constraint, DmlConstraintKind.Check);
        yield return Row(Databricks, new MessageEx("[DELTA_CONSTRAINT_ALREADY_EXISTS] x"), Constraint, DmlConstraintKind.Check);
        yield return Row(Databricks, new MessageEx("[delta_not_null_constraint_violated] x"), Provider);
        yield return Row(Databricks, new MessageEx("[UNRESOLVED_COLUMN.WITH_SUGGESTION] x"), Denied);
        yield return Row(Databricks, new MessageEx("[unresolved_column] x"), Provider);
        yield return Row(Databricks, new MessageEx("[NUM_COLUMNS_MISMATCH] x"), Syntax);
        yield return Row(Databricks, new MessageEx("[num_columns_mismatch] x"), Provider);
        foreach (string m in new[] { "CAST_INVALID_INPUT", "CAST_OVERFLOW", "ARITHMETIC_OVERFLOW", "NUMERIC_VALUE_OUT_OF_RANGE", "INVALID_ARRAY_INDEX", "DELTA_EXCEED_CHAR_VARCHAR_LIMIT" })
        {
            yield return Row(Databricks, new MessageEx($"[{m}] x"), Data);
            yield return Row(Databricks, new MessageEx($"[{m.ToLowerInvariant()}] x"), Provider);
        }

        // a dialect without a classification table and the cross-dialect isolation of the tables
        foreach (var dialect in new[] { Ansi })
        {
            yield return Row(dialect, new NumEx(2627), Provider);
            yield return Row(dialect, new StateEx("23505"), Provider);
            yield return Row(dialect, new MessageEx("Duplicate key"), Provider);
            yield return Row(dialect, new MessageEx("CAST_OVERFLOW"), Provider);
        }

        yield return Row(SqlServer, new StateEx("23505"), Provider);
        yield return Row(SqlServer, new MessageEx("Duplicate key"), Provider);
        yield return Row(Oracle, new MessageEx("Duplicate key"), Provider);
        yield return Row(PostgreSql, new MessageEx("Duplicate key"), Provider);
        yield return Row(DuckDb, new NumEx(2627), Provider);
        yield return Row(Databricks, new MessageEx("Duplicate key"), Provider);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Map_ClassifiesTheProviderError(TargetSqlDialect dialect, Exception error, string code, DmlConstraintKind? kind)
    {
        var mapped = DmlErrorSanitizer.Map(dialect, error);

        Assert.Equal(code, mapped.Code);
        Assert.Equal(dialect, mapped.Dialect);
        Assert.Equal(GovernedSqlErrorCodes.FixedMessage(code), mapped.Message);
        Assert.Null(mapped.InnerException);
        if (kind is { } expected)
        {
            var violation = Assert.IsType<DmlConstraintViolationException>(mapped);
            Assert.Equal(expected, violation.Kind);
            Assert.Equal($"{dialect}:{expected}", DmlErrorSanitizer.Describe(violation));
            var tryMapped = DmlErrorSanitizer.TryMap(dialect, error);
            Assert.NotNull(tryMapped);
            Assert.Equal(expected, tryMapped.Kind);
        }
        else
        {
            Assert.IsNotType<DmlConstraintViolationException>(mapped);
            Assert.Null(DmlErrorSanitizer.TryMap(dialect, error));
        }
    }

    [Fact]
    public void ErrorsAreFoundThroughTheInnerExceptionChain()
    {
        var inner = new NumEx(2627);
        var outer = new MessageEx("outer", new MessageEx("middle", inner));
        Assert.Equal(DmlConstraintKind.Unique, DmlErrorSanitizer.TryMap(SqlServer, outer)!.Kind);
        Assert.Equal(GovernedSqlErrorCodes.ConstraintViolation, DmlErrorSanitizer.Map(SqlServer, outer).Code);

        Assert.Equal(Limit, DmlErrorSanitizer.Map(SqlServer, new MessageEx("o", new MessageEx("m", new NumEx(530)))).Code);
        Assert.Equal(Denied, DmlErrorSanitizer.Map(SqlServer, new MessageEx("o", new MessageEx("m", new NumEx(207)))).Code);
        Assert.Equal(Syntax, DmlErrorSanitizer.Map(SqlServer, new MessageEx("o", new MessageEx("m", new NumEx(205)))).Code);
        Assert.Equal(Data, DmlErrorSanitizer.Map(SqlServer, new MessageEx("o", new MessageEx("m", new NumEx(8152)))).Code);
        Assert.Equal(Provider, DmlErrorSanitizer.Map(SqlServer, new MessageEx("o", new MessageEx("m", new NumEx(3)))).Code);
    }

    [Fact]
    public void Precedence_ConstraintThenLimitThenUnknownColumnThenDegreeThenData()
    {
        // an outer data error does not hide an inner constraint, an outer limit error does not hide an inner unknown column, and so on
        var constraintInsideData = new NumEx(8152, "outer") ;
        Assert.Equal(Data, DmlErrorSanitizer.Map(SqlServer, constraintInsideData).Code);
        Assert.Equal(Constraint, DmlErrorSanitizer.Map(SqlServer, new NumWrap(new NumEx(2627), 8152)).Code);
        Assert.Equal(Limit, DmlErrorSanitizer.Map(SqlServer, new NumWrap(new NumEx(530), 207)).Code);
        Assert.Equal(Denied, DmlErrorSanitizer.Map(SqlServer, new NumWrap(new NumEx(207), 205)).Code);
        Assert.Equal(Syntax, DmlErrorSanitizer.Map(SqlServer, new NumWrap(new NumEx(205), 8152)).Code);
        Assert.Equal(Data, DmlErrorSanitizer.Map(SqlServer, new NumWrap(new NumEx(8152), 3)).Code);
    }

    private sealed class NumWrap(Exception inner, int number) : Exception("wrap", inner)
    {
        public int Number { get; } = number;
    }

    [Fact]
    public void NullErrors_AreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => DmlErrorSanitizer.TryMap(SqlServer, null!));
        Assert.Throws<ArgumentNullException>(() => DmlErrorSanitizer.Map(SqlServer, null!));
    }

    [Theory]
    [InlineData(DmlConstraintKind.Unique)]
    [InlineData(DmlConstraintKind.ForeignKey)]
    [InlineData(DmlConstraintKind.NotNull)]
    [InlineData(DmlConstraintKind.Check)]
    [InlineData(DmlConstraintKind.MergeMultipleMatches)]
    public void ConstraintViolation_CarriesKindDialectAndTheFixedMessage(DmlConstraintKind kind)
    {
        var violation = new DmlConstraintViolationException(kind, Oracle);

        Assert.Equal(kind, violation.Kind);
        Assert.Equal(Oracle, violation.Dialect);
        Assert.Equal(GovernedSqlErrorCodes.ConstraintViolation, violation.Code);
        Assert.Equal("The statement violated a data constraint.", violation.Message);
        Assert.Equal($"Oracle:{kind}", DmlErrorSanitizer.Describe(violation));
        Assert.Equal(new[] { "Unique", "ForeignKey", "NotNull", "Check", "MergeMultipleMatches" }, Enum.GetNames<DmlConstraintKind>());
    }
}
