namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using System.Data;
using System.Data.Common;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

/// <summary>
/// Everything the loader needs for one key query. <see cref="Connection"/> is the governed read connection of the data source; its provider must
/// match <see cref="Dialect"/> (the catalog dialect). <see cref="Converter"/> converts the loaded keys to the foreign key type (SEC-RF107-07).
/// </summary>
public sealed record RowFilterKeySetLoadRequest(
    DataSourceConnectionOptions Connection,
    TargetSqlDialect Dialect,
    TenantId Tenant,
    string DataSourceName,
    CompiledKeyQuery Query,
    KeySetValueConverter Converter,
    int MaxKeys,
    TimeSpan Timeout);

/// <summary>
/// Issue #107 (PLAN-GOV-107 A-11): runs one compiled key query on the governed read connection of a data source.
/// <list type="bullet">
/// <item>The session is tenant only: <c>userSid: null, purpose: null</c> (SEC-RF107-01 b); the key set is principal independent.</item>
/// <item>SQL Server: <c>SET TRANSACTION ISOLATION LEVEL READ COMMITTED</c> before the query, regardless of <c>ReadUncommitted</c> (SEC-RF107-12), so
/// uncommitted rows are never cached. PostgreSQL and Oracle: <c>SET TRANSACTION READ ONLY</c>, always rolled back. SQLite: <c>PRAGMA query_only</c>.</item>
/// <item>Every <see cref="CompiledKeyQuery.Parameters"/> element is bound through the engine's production binder (<see cref="CompiledSqlBinders.BindKeyQuery"/>; the SQLite row limit is a
/// <c>QueryLiteral</c> bind) and the command's parameter count is checked before execution (SEC-RF107-20, D-WPC-8).</item>
/// <item>At most <c>MaxKeys + 1</c> rows are read; one more than <c>MaxKeys</c> yields the overflow sentinel. A set with a value that does not fit
/// the foreign key type yields the not-bindable sentinel (SEC-RF107-07 b).</item>
/// <item><see cref="RowFilterKeySetEntry.LoadedAt"/> is taken before anything else, so before the key query starts (SEC-RF107-04 b).</item>
/// <item>Any exception (connection, session, command, read, timeout, conversion) is a <c>ROW_FILTER_KEY_SET_UNAVAILABLE</c> denial with a fixed
/// message; the cause is attached as inner exception only, the Warning carries the exception type only, partial rows are discarded, nothing is
/// cached (SEC-RF107-08).</item>
/// </list>
/// </summary>
public sealed class RowFilterKeySetLoader
{
    private readonly ISqlConnectionFactory _connections;
    private readonly IDbSessionContextInitializer _sessionInitializer;
    private readonly TimeProvider _time;
    private readonly ILogger<RowFilterKeySetLoader>? _logger;

    public RowFilterKeySetLoader(
        ISqlConnectionFactory connections,
        IDbSessionContextInitializer? sessionInitializer = null,
        TimeProvider? timeProvider = null,
        ILogger<RowFilterKeySetLoader>? logger = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _sessionInitializer = sessionInitializer ?? new DbSessionContextInitializer();
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <exception cref="Autheris.Domain.Exceptions.GatewaySecurityException">Code <see cref="RowFilterKeySetErrorCodes.KeySetUnavailable"/>.</exception>
    public async Task<RowFilterKeySetEntry> LoadAsync(RowFilterKeySetLoadRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loadedAt = _time.GetUtcNow();
        try
        {
            var rows = await ReadAsync(request, ct).ConfigureAwait(false);
            string tenant = request.Tenant.Value;
            if (rows is null)
            {
                return RowFilterKeySetEntry.OverflowSentinel(loadedAt, tenant, request.DataSourceName);
            }

            return request.Converter.TryConvertAll(rows, out var keys)
                ? new RowFilterKeySetEntry(keys, false, loadedAt, tenant, request.DataSourceName)
                : RowFilterKeySetEntry.NotBindableSentinel(loadedAt, tenant, request.DataSourceName);
        }
        catch (Exception ex)
        {
            // SEC-RF107-08: database messages can quote data; only the type is logged, the cause travels as inner exception.
            _logger?.LogWarning("The row-filter key set could not be loaded ({ExceptionType}); the statement is denied.", ex.GetType().Name);
            throw RowFilterKeySetErrorCodes.Unavailable(ex);
        }
    }

    /// <summary>The raw key values, or <c>null</c> when more than <c>MaxKeys</c> rows exist (overflow).</summary>
    private async Task<List<object?>?> ReadAsync(RowFilterKeySetLoadRequest request, CancellationToken ct)
    {
        var dialect = ToDatabaseDialect(request.Dialect);
        if (!DataSourceProvider.TryResolveDialect(request.Connection.Provider, out var connectionDialect) || connectionDialect != dialect)
        {
            throw new InvalidOperationException("The provider of the data source connection does not match the dialect of the key query.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxKeys, 1);
        var query = request.Query;
        await using var connection = await _connections.CreateOpenConnectionAsync(request.Connection, ct).ConfigureAwait(false);

        // PostgreSQL and Oracle: a read-only transaction that is never committed (the key query must not change data).
        await using DbTransaction? tx = dialect is DatabaseDialect.PostgreSql or DatabaseDialect.Oracle
            ? await connection.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        bool queryOnly = false;
        try
        {
            if (tx != null)
            {
                await ExecuteAsync(connection, tx, "SET TRANSACTION READ ONLY", ct).ConfigureAwait(false);
            }
            else if (dialect == DatabaseDialect.SqlServer)
            {
                // SEC-RF107-12: ReadUncommitted of the source applies to user statements; a cached key set must never hold dirty rows.
                await ExecuteAsync(connection, null, "SET TRANSACTION ISOLATION LEVEL READ COMMITTED", ct).ConfigureAwait(false);
            }
            else if (dialect == DatabaseDialect.Sqlite)
            {
                await ExecuteAsync(connection, null, "PRAGMA query_only = ON", ct).ConfigureAwait(false);
                queryOnly = true;
            }

            // SEC-RF107-01 b: principal independent; the session carries the tenant only.
            await _sessionInitializer.InitializeSessionAsync(connection, tx, dialect, request.Tenant, userSid: null, purpose: null, ct: ct).ConfigureAwait(false);

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandType = CommandType.Text;
            cmd.CommandTimeout = (int)Math.Max(1, Math.Ceiling(request.Timeout.TotalSeconds));
            CompiledSqlBinders.BindKeyQuery(cmd, query, request.Dialect);

            // SEC-RF107-20: a bind that was not added would silently change the query (D-WPC-8).
            if (cmd.Parameters.Count != query.Parameters.Length)
            {
                throw new InvalidOperationException("The bound parameter count does not match the compiled key query.");
            }

            var rows = new List<object?>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (rows.Count >= request.MaxKeys)
                    {
                        TryCancel(cmd);
                        return null;   // MaxKeys + 1 rows read: overflow
                    }

                    rows.Add(await reader.IsDBNullAsync(0, ct).ConfigureAwait(false) ? null : reader.GetValue(0));
                }
            }

            return rows;
        }
        finally
        {
            if (tx != null)
            {
                await TryAsync(() => tx.RollbackAsync(CancellationToken.None)).ConfigureAwait(false);
            }

            if (queryOnly)
            {
                // A pooled SQLite connection keeps its pragmas; later users must not inherit a read-only session.
                await TryAsync(() => ExecuteAsync(connection, null, "PRAGMA query_only = OFF", CancellationToken.None)).ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void TryCancel(DbCommand cmd)
    {
        try
        {
            cmd.Cancel();
        }
        catch (Exception)
        {
            // best effort: the reader is disposed right after
        }
    }

    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // cleanup only; the original outcome (result or failure) stands
        }
    }

    private static DatabaseDialect ToDatabaseDialect(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => DatabaseDialect.SqlServer,
        TargetSqlDialect.PostgreSql => DatabaseDialect.PostgreSql,
        TargetSqlDialect.Oracle => DatabaseDialect.Oracle,
        TargetSqlDialect.Sqlite => DatabaseDialect.Sqlite,
        _ => throw new NotSupportedException("The key-set cache has no loader for this dialect.")
    };
}
