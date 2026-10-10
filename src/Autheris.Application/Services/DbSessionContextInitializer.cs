namespace Autheris.Application.Services;

using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;

/// <summary>
/// M-1: Centralizes database session context and transaction management across all database access paths.
/// </summary>
public sealed class DbSessionContextInitializer : IDbSessionContextInitializer
{
    public static DatabaseDialect ResolveDialect(string? provider)
    {
        if (ProcedureConnectionProvider.TryResolveDialect(provider, out var dialect))
        {
            return dialect;
        }

        // SEC-ADG-14: an unknown provider must never fall back to another dialect (fail closed).
        throw new NotSupportedException($"SQL provider '{provider}' is not supported for session initialization.");
    }

    public Task<DbTransaction?> InitializeSessionAsync(
        DbConnection connection,
        string? provider,
        TenantId tenantId,
        string? userSid = null,
        string? purpose = null,
        bool requireTransaction = true,
        CancellationToken ct = default)
    {
        var dialect = ResolveDialect(provider);
        return InitializeSessionAsync(connection, dialect, tenantId, userSid, purpose, requireTransaction, ct);
    }

    public async Task<DbTransaction?> InitializeSessionAsync(
        DbConnection connection,
        DatabaseDialect dialect,
        TenantId tenantId,
        string? userSid = null,
        string? purpose = null,
        bool requireTransaction = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (dialect == DatabaseDialect.PostgreSql)
        {
            if (requireTransaction)
            {
                var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                try
                {
                    await ExecutePostgreSqlInitAsync(connection, tx, isLocal: true, tenantId, userSid, purpose, ct).ConfigureAwait(false);
                    return tx;
                }
                catch
                {
                    try
                    {
                        await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // suppress rollback failure to ensure original exception is preserved
                    }
                    finally
                    {
                        await tx.DisposeAsync().ConfigureAwait(false);
                    }
                    throw;
                }
            }
            else
            {
                await ExecutePostgreSqlInitAsync(connection, tx: null, isLocal: false, tenantId, userSid, purpose, ct).ConfigureAwait(false);
                return null;
            }
        }

        if (dialect == DatabaseDialect.SqlServer)
        {
            await ExecuteSqlServerInitAsync(connection, tx: null, tenantId, userSid, purpose, ct).ConfigureAwait(false);
            return null;
        }

        if (HasNoSessionState(dialect))
        {
            return null;
        }

        throw UnsupportedSessionDialect(dialect);
    }

    public async Task InitializeSessionAsync(
        DbConnection connection,
        DbTransaction? tx,
        DatabaseDialect dialect,
        TenantId tenantId,
        string? userSid = null,
        string? purpose = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (dialect == DatabaseDialect.PostgreSql)
        {
            bool isLocal = tx != null;
            await ExecutePostgreSqlInitAsync(connection, tx, isLocal, tenantId, userSid, purpose, ct).ConfigureAwait(false);
        }
        else if (dialect == DatabaseDialect.SqlServer)
        {
            await ExecuteSqlServerInitAsync(connection, tx, tenantId, userSid, purpose, ct).ConfigureAwait(false);
        }
        else if (!HasNoSessionState(dialect))
        {
            throw UnsupportedSessionDialect(dialect);
        }
    }

    /// <summary>Explicit "no session state" dialects: SQLite is a file-local engine, Databricks is stateless over HTTP.</summary>
    private static bool HasNoSessionState(DatabaseDialect dialect) =>
        dialect is DatabaseDialect.Sqlite or DatabaseDialect.Databricks;

    private static NotSupportedException UnsupportedSessionDialect(DatabaseDialect dialect) =>
        new($"Session initialization for dialect '{dialect}' is not supported (fail closed).");

    private static async Task ExecutePostgreSqlInitAsync(
        DbConnection connection,
        DbTransaction? tx,
        bool isLocal,
        TenantId tenantId,
        string? userSid,
        string? purpose,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = isLocal
            ? "SET standard_conforming_strings = on; " +
              "SELECT set_config('autheris.tenant_id', @tenant, true), " +
              "set_config('app.tenant_id', @tenant, true), " +
              "set_config('autheris.user_sid', @sid, true), " +
              "set_config('autheris.purpose', @purpose, true), " +
              "set_config('TimeZone', 'UTC', true);"
            : "SET standard_conforming_strings = on; " +
              "SELECT set_config('autheris.tenant_id', @tenant, false), " +
              "set_config('app.tenant_id', @tenant, false), " +
              "set_config('autheris.user_sid', @sid, false), " +
              "set_config('autheris.purpose', @purpose, false), " +
              "set_config('TimeZone', 'UTC', false);";

        AddParameter(cmd, "@tenant", tenantId.Value);
        AddParameter(cmd, "@sid", userSid ?? string.Empty);
        AddParameter(cmd, "@purpose", purpose ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExecuteSqlServerInitAsync(
        DbConnection connection,
        DbTransaction? tx,
        TenantId tenantId,
        string? userSid,
        string? purpose,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "EXEC sys.sp_set_session_context @key = N'autheris.tenant_id', @value = @tenant, @read_only = 1; " +
            "EXEC sys.sp_set_session_context @key = N'autheris.user_sid', @value = @sid, @read_only = 1; " +
            "EXEC sys.sp_set_session_context @key = N'autheris.purpose', @value = @purpose, @read_only = 1;";

        AddParameter(cmd, "@tenant", tenantId.Value);
        AddParameter(cmd, "@sid", userSid ?? string.Empty);
        AddParameter(cmd, "@purpose", purpose ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
