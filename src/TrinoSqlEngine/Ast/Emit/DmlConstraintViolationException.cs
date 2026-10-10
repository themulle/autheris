namespace TrinoSqlEngine.Ast.Emit;

using System;
using System.Data.Common;
using System.Globalization;
using System.Reflection;

/// <summary>What kind of data constraint a DML statement violated (no names, no values).</summary>
public enum DmlConstraintKind
{
    Unique,
    ForeignKey,
    NotNull,
    Check,

    /// <summary>MERGE: more than one source row matched one target row (Delta, SQL Server, PostgreSQL, Oracle).</summary>
    MergeMultipleMatches
}

/// <summary>
/// A DML or MERGE statement violated a constraint. SEC-ADG-08 d / R-12: a unique-key violation caused by another tenant's row is
/// the one side channel that tenant-scoped DML cannot remove (it reveals that the key exists), so the error that reaches the caller
/// is this typed exception with a fixed message: no driver text, no key value, no constraint, table or schema name, and no inner
/// exception (the inner exception would carry the driver message). The original error stays in the server-side log only.
/// </summary>
public sealed class DmlConstraintViolationException : Exception
{
    public DmlConstraintKind Kind { get; }

    public TargetSqlDialect Dialect { get; }

    public DmlConstraintViolationException(DmlConstraintKind kind, TargetSqlDialect dialect)
        : base("The statement violated a data constraint.")
    {
        Kind = kind;
        Dialect = dialect;
    }
}

/// <summary>
/// Classifies the provider exception of an executed DML statement without referencing any provider package: the SQL Server
/// <c>Number</c>, the PostgreSQL SQLSTATE, the Oracle <c>Number</c>, the DuckDB message class and the Delta error class.
/// Anything that is not a constraint violation returns null and stays the caller's concern (it is logged, never echoed).
/// </summary>
public static class DmlErrorSanitizer
{
    /// <summary>The typed, value-free error for <paramref name="error"/>, or null when it is not a constraint violation.</summary>
    public static DmlConstraintViolationException? TryMap(TargetSqlDialect dialect, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (Classify(dialect, current) is { } kind) return new DmlConstraintViolationException(kind, dialect);
        }

        return null;
    }

    private static DmlConstraintKind? Classify(TargetSqlDialect dialect, Exception error) => dialect switch
    {
        TargetSqlDialect.SqlServer => Number(error) switch
        {
            2627 or 2601 => DmlConstraintKind.Unique,
            547 => DmlConstraintKind.ForeignKey,       // FOREIGN KEY or CHECK
            515 => DmlConstraintKind.NotNull,
            8672 => DmlConstraintKind.MergeMultipleMatches,
            _ => null
        },
        TargetSqlDialect.PostgreSql => (error is DbException db ? db.SqlState : StringProperty(error, "SqlState")) switch
        {
            "23505" => DmlConstraintKind.Unique,
            "23503" => DmlConstraintKind.ForeignKey,
            "23502" => DmlConstraintKind.NotNull,
            "23514" => DmlConstraintKind.Check,
            "21000" => DmlConstraintKind.MergeMultipleMatches,
            _ => null
        },
        TargetSqlDialect.Oracle => Number(error) switch
        {
            1 => DmlConstraintKind.Unique,
            2291 or 2292 => DmlConstraintKind.ForeignKey,
            1400 or 1407 => DmlConstraintKind.NotNull,
            2290 => DmlConstraintKind.Check,
            30926 => DmlConstraintKind.MergeMultipleMatches,
            _ => null
        },
        TargetSqlDialect.DuckDb => ClassifyMessage(error.Message),
        TargetSqlDialect.Databricks => ClassifyDelta(error.Message),
        _ => null
    };

    private static DmlConstraintKind? ClassifyMessage(string message)
    {
        if (message.Contains("Duplicate key", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("violates primary key constraint", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("violates unique constraint", StringComparison.OrdinalIgnoreCase))
        {
            return DmlConstraintKind.Unique;
        }

        if (message.Contains("NOT NULL constraint failed", StringComparison.OrdinalIgnoreCase)) return DmlConstraintKind.NotNull;
        if (message.Contains("violates foreign key", StringComparison.OrdinalIgnoreCase)) return DmlConstraintKind.ForeignKey;
        if (message.Contains("CHECK constraint failed", StringComparison.OrdinalIgnoreCase)) return DmlConstraintKind.Check;
        if (message.Contains("MERGE INTO", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("multiple", StringComparison.OrdinalIgnoreCase)) return DmlConstraintKind.MergeMultipleMatches;
        return null;
    }

    private static DmlConstraintKind? ClassifyDelta(string message)
    {
        if (message.Contains("DELTA_MULTIPLE_SOURCE_ROW_MATCHING_TARGET_ROW_IN_MERGE", StringComparison.Ordinal)) return DmlConstraintKind.MergeMultipleMatches;
        if (message.Contains("DELTA_NOT_NULL_CONSTRAINT_VIOLATED", StringComparison.Ordinal) ||
            message.Contains("DELTA_NON_NULLABLE_COLUMN_OMITTED", StringComparison.Ordinal)) return DmlConstraintKind.NotNull;
        if (message.Contains("DELTA_VIOLATE_CONSTRAINT_WITH_VALUES", StringComparison.Ordinal) ||
            message.Contains("DELTA_CONSTRAINT_ALREADY_EXISTS", StringComparison.Ordinal)) return DmlConstraintKind.Check;
        return null;
    }

    private static int? Number(Exception error) =>
        error.GetType().GetProperty("Number", BindingFlags.Public | BindingFlags.Instance)?.GetValue(error) is int n ? n : null;

    private static string? StringProperty(Exception error, string name) =>
        error.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(error) as string;

    /// <summary>For logs only: the kind without any driver text.</summary>
    public static string Describe(DmlConstraintViolationException error) =>
        string.Create(CultureInfo.InvariantCulture, $"{error.Dialect}:{error.Kind}");
}
