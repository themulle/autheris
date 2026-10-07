using System.ComponentModel;
using System.Data.Common;

namespace Autheris.Application.Common;

public enum DataAccessErrorKind
{
    Other = 0,
    /// <summary>The statement exceeded its time limit (504).</summary>
    Timeout = 1,
    /// <summary>The data source is temporarily unavailable or overloaded: deadlock, failover, pool exhausted (503).</summary>
    Unavailable = 2
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
        [1205, 4060, 40613, 40501, 40197, 40540, 40143, 49918, 49919, 49920, 10928, 10929, 10053, 10054, 10060, 233, 64, 53, -1];

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
                return DataAccessErrorKind.Other;
        }
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
            if (sqlState is "40001" or "40P01" or "53300" or "57P01" or "57P02" or "57P03" or "55P03" ||
                sqlState.StartsWith("08", StringComparison.Ordinal))
            {
                // serialization failure, deadlock, too many connections, admin shutdown, cannot connect now,
                // lock not available, connection exceptions
                return DataAccessErrorKind.Unavailable;
            }
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
