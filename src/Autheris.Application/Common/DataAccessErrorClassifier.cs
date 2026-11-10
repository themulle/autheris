using System.ComponentModel;
using System.Data.Common;

namespace Autheris.Application.Common;

public enum DataAccessErrorKind
{
    Other = 0,
    /// <summary>The statement exceeded its time limit (504).</summary>
    Timeout = 1,
    /// <summary>The data source is temporarily unavailable or overloaded: deadlock, failover, pool exhausted (503).</summary>
    Unavailable = 2,
    /// <summary>The data source rejected a statement because a declared table, column or routine no longer matches it (502, drift).</summary>
    SourceSchemaMismatch = 3,
    /// <summary>The source login lacks a privilege the governed statement needs (502).</summary>
    SourcePermissionDenied = 4,
    /// <summary>The data source rejected the statement due to syntax, invalid argument, or conversion error (400).</summary>
    StatementRejected = 5
}

/// <summary>Stable codes of the drift errors. The messages are generic: no object or column names, no SQL.</summary>
public static class SourceDriftErrors
{
    public const string SchemaMismatchCode = "SOURCE_SCHEMA_MISMATCH";
    public const string PermissionDeniedCode = "SOURCE_PERMISSION_DENIED";
    public const string SchemaMismatchMessage = "The data source no longer matches the declared catalog definition. Upload the current definition.";
    public const string PermissionDeniedMessage = "The data source login lacks a privilege that the governed statement needs.";
}

/// <summary>
/// O7 (docs/plans/rls-subquery-in-strategy.md): classifies database errors by provider error number (SQL Server
/// <c>SqlException.Number</c>, read by name so Application stays provider-neutral), SQLSTATE (PostgreSQL) and
/// <see cref="DbException.IsTransient"/>. The message is only consulted for providers that expose neither.
/// </summary>
public static class DataAccessErrorClassifier
{
    // SQL Server: deadlock, login/database unavailable, Azure SQL throttling and failover, broken connections.
    private static readonly HashSet<int> SqlServerUnavailableNumbers =
        [1205, 4060, 40613, 40501, 40197, 40540, 40143, 49918, 49919, 49920, 10928, 10929, 10053, 10054, 10060, 10061, 10051, 10065, 11001, 233, 64, 53, 2, -1, 18456, 18452, 18487, 18488, 40615];

    private static readonly HashSet<int> SqlServerMismatchNumbers = [207, 208, 2812, 201, 8144];
    private static readonly HashSet<int> SqlServerPermissionNumbers = [229, 230, 262];
    private static readonly HashSet<int> OracleMismatchNumbers = [942, 904, 6550, 6553];
    private static readonly HashSet<string> PostgreSqlMismatchStates = ["42P01", "42703", "42883", "42804"];
    private static readonly HashSet<string> DatabricksMismatchClasses = ["TABLE_OR_VIEW_NOT_FOUND", "UNRESOLVED_COLUMN", "UNRESOLVED_ROUTINE"];
    private static readonly HashSet<string> DatabricksPermissionClasses = ["PERMISSION_DENIED", "INSUFFICIENT_PERMISSIONS"];
    private static readonly HashSet<int> SqlServerRejectedNumbers =
        [102, 103, 105, 156, 170, 178, 8116, 8145, 8146, 8151, 195, 137, 245, 8114, 8115, 241, 242, 220, 232, 295, 9803];
    private static readonly HashSet<string> PostgreSqlRejectedStates =
        ["42601", "22001", "22003", "22007", "22008", "22012", "22P02"];
    private static readonly HashSet<int> OracleRejectedNumbers =
        [900, 923, 933, 936, 12899, 1722, 1858, 1861, 1840, 1843, 1438, 1401, 1476, 6502, 1830];
    private static readonly HashSet<string> DatabricksRejectedClasses =
        ["PARSE_SYNTAX_ERROR", "WRONG_NUM_ARGS", "CAST_INVALID_INPUT", "CAST_OVERFLOW", "ARITHMETIC_OVERFLOW", "NUMERIC_VALUE_OUT_OF_RANGE"];

    private const int SqlServerTimeoutNumber = -2;
    private const int Win32WaitTimeout = 258;

    public static DataAccessErrorKind Classify(Exception? exception)
    {
        var result = DataAccessErrorKind.Other;
        foreach (var ex in Flatten(exception))
        {
            var kind = ClassifySingle(ex);
            if (kind == DataAccessErrorKind.Timeout)
            {
                return kind;
            }
            if (kind == DataAccessErrorKind.Unavailable)
            {
                result = kind;
            }
            else if (kind > DataAccessErrorKind.Unavailable && result == DataAccessErrorKind.Other)
            {
                result = kind;
            }
        }
        return result;
    }

    private static DataAccessErrorKind ClassifySingle(Exception ex)
    {
        switch (ex)
        {
            case TimeoutException:
                return DataAccessErrorKind.Timeout;
            case Win32Exception { NativeErrorCode: Win32WaitTimeout }:
                return DataAccessErrorKind.Timeout;
            case DbException db:
                return ClassifyDbException(db);
            case InvalidOperationException when ex.Message.Contains("obtaining a connection from the pool", StringComparison.OrdinalIgnoreCase):
                // SqlClient/Npgsql: pool exhausted -> overload, not a slow statement
                return DataAccessErrorKind.Unavailable;
            default:
                // the Databricks facade surfaces the REST error class (read by name; Application stays provider-neutral)
                return ex.GetType().GetProperty("ErrorClass")?.GetValue(ex) is string errorClass ? ClassifyDatabricks(errorClass) : DataAccessErrorKind.Other;
        }
    }

    private static DataAccessErrorKind ClassifyDatabricks(string errorClass) =>
        DatabricksMismatchClasses.Contains(errorClass) ? DataAccessErrorKind.SourceSchemaMismatch
        : DatabricksPermissionClasses.Contains(errorClass) ? DataAccessErrorKind.SourcePermissionDenied
        : DatabricksRejectedClasses.Contains(errorClass) ? DataAccessErrorKind.StatementRejected
        : DataAccessErrorKind.Other;

    private static DataAccessErrorKind ClassifyDrift(DbException db, int? number, string? sqlState)
    {
        if (number is { } n)
        {
            var oracle = db.GetType().FullName?.StartsWith("Oracle.", StringComparison.Ordinal) == true;
            if (oracle ? OracleMismatchNumbers.Contains(n) : SqlServerMismatchNumbers.Contains(n))
            {
                return DataAccessErrorKind.SourceSchemaMismatch;
            }

            if (oracle ? n == 1031 : SqlServerPermissionNumbers.Contains(n))
            {
                return DataAccessErrorKind.SourcePermissionDenied;
            }
        }

        return sqlState is null ? DataAccessErrorKind.Other
            : PostgreSqlMismatchStates.Contains(sqlState) ? DataAccessErrorKind.SourceSchemaMismatch
            : sqlState == "42501" ? DataAccessErrorKind.SourcePermissionDenied
            : DataAccessErrorKind.Other;
    }

    private static DataAccessErrorKind ClassifyDbException(DbException db)
    {
        var number = TryGetErrorNumber(db);
        if (number.HasValue)
        {
            if (number.Value == SqlServerTimeoutNumber)
            {
                return DataAccessErrorKind.Timeout;
            }
            if (SqlServerUnavailableNumbers.Contains(number.Value))
            {
                return DataAccessErrorKind.Unavailable;
            }
        }

        var sqlState = db.SqlState;
        if (!string.IsNullOrEmpty(sqlState))
        {
            if (sqlState == "57014")
            {
                // query_canceled (statement_timeout)
                return DataAccessErrorKind.Timeout;
            }
            if (sqlState is "40001" or "40P01" or "53300" or "57P01" or "57P02" or "57P03" or "55P03" or "28000" or "28P01" ||
                sqlState.StartsWith("08", StringComparison.Ordinal))
            {
                // serialization failure, deadlock, too many connections, admin shutdown, cannot connect now,
                // lock not available, connection exceptions, authentication failure
                return DataAccessErrorKind.Unavailable;
            }
        }

        var drift = ClassifyDrift(db, number, sqlState);
        if (drift != DataAccessErrorKind.Other)
        {
            return drift;
        }

        if (number.HasValue)
        {
            var oracle = db.GetType().FullName?.StartsWith("Oracle.", StringComparison.Ordinal) == true;
            if (oracle ? OracleRejectedNumbers.Contains(number.Value) : SqlServerRejectedNumbers.Contains(number.Value))
            {
                return DataAccessErrorKind.StatementRejected;
            }
        }

        if (!string.IsNullOrEmpty(sqlState) && PostgreSqlRejectedStates.Contains(sqlState))
        {
            return DataAccessErrorKind.StatementRejected;
        }

        if (db.IsTransient)
        {
            return DataAccessErrorKind.Unavailable;
        }

        // Providers without error number and SQLSTATE: the message is the only hint left.
        if (!number.HasValue && string.IsNullOrEmpty(sqlState) &&
            db.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return DataAccessErrorKind.Timeout;
        }

        return DataAccessErrorKind.Other;
    }

    private static int? TryGetErrorNumber(DbException db)
    {
        var property = db.GetType().GetProperty("Number");
        return property?.PropertyType == typeof(int) ? (int?)property.GetValue(db) : null;
    }

    private static IEnumerable<Exception> Flatten(Exception? exception)
    {
        var pending = new Stack<Exception>();
        if (exception != null)
        {
            pending.Push(exception);
        }

        var depth = 0;
        while (pending.Count > 0 && depth++ < 32)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException != null)
            {
                pending.Push(current.InnerException);
            }
        }
    }
}
