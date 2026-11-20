using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence.Migrations.Shape;

/// <summary>
/// Bounded retry for transient SQL Server error 102 ("incorrect syntax") on static read-only shape-verify catalog queries.
/// Never used for DDL steps, post-conditions, or history writes.
/// </summary>
internal static partial class ShapeReadRetryPolicy
{
    private const int SqlSyntaxErrorNumber = 102;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200)];

    internal static readonly string SqlClientVersion = ResolveSqlClientVersion();

    internal static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> read,
        Func<CancellationToken, Task<bool>> transactionUsable,
        ILogger logger,
        string statement,
        CancellationToken ct,
        Func<Exception, (int Number, int LineNumber)?>? extractSqlError = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(transactionUsable);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(statement);

        var extract = extractSqlError ?? ExtractSqlServerError;
        var clock = timeProvider ?? TimeProvider.System;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await read(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts
                && !ct.IsCancellationRequested
                && extract(ex) is { Number: SqlSyntaxErrorNumber } error)
            {
                if (!await transactionUsable(ct).ConfigureAwait(false))
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }

                LogRetry(logger, error.Number, error.LineNumber, attempt, SqlClientVersion, statement);
                await Task.Delay(RetryDelays[attempt - 1], clock, ct).ConfigureAwait(false);
            }
        }
    }

    private static (int Number, int LineNumber)? ExtractSqlServerError(Exception ex) =>
        ex is SqlException sql ? (sql.Number, sql.LineNumber) : null;

    private static string ResolveSqlClientVersion() =>
        typeof(SqlException).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SqlException).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "SQL Server shape-verify catalog read failed with error {ErrorNumber} at line {LineNumber}; retrying after attempt {Attempt} of 3 (SqlClient {SqlClientVersion}). Statement: {Statement}")]
    private static partial void LogRetry(
        ILogger logger,
        int errorNumber,
        int lineNumber,
        int attempt,
        string sqlClientVersion,
        string statement);
}
