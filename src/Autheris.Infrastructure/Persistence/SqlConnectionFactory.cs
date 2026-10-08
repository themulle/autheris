using System.Data.Common;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Autheris.Infrastructure.Persistence;

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    /// <summary>RR-L5-01: Session settings required by the literal escaping in <c>DatabaseDialect.EscapeSqlLiteral</c>.</summary>
    public const string PostgreSqlSessionInitializationSql = "SET standard_conforming_strings = on";

    /// <summary>Dirty read for SQL Server sessions of data sources with <c>ReadUncommitted</c> (reads take no shared locks).</summary>
    public const string SqlServerReadUncommittedSql = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED";

    public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException("Connection string cannot be empty for SQL data source.", nameof(options));
        }

        if (!DataSourceProvider.TryResolveDialect(options.Provider, out var dialect))
        {
            throw UnsupportedProvider(options.Provider);
        }

        // Architecture 5: Oracle and Databricks are dialects without a driver here.
        DbConnection connection = dialect switch
        {
            DatabaseDialect.Sqlite => new SqliteConnection(options.ConnectionString),
            DatabaseDialect.SqlServer => new SqlConnection(options.ConnectionString),
            DatabaseDialect.PostgreSql => new Npgsql.NpgsqlConnection(options.ConnectionString),
            _ => throw UnsupportedProvider(options.Provider)
        };

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            if (connection is Npgsql.NpgsqlConnection pgConn)
            {
                // RR-L5-01: Row-filter literals are escaped for standard_conforming_strings = on (backslashes are
                // literal). Enforce that mode on every session instead of relying on the server default; with "off"
                // a trailing backslash would escape the closing quote (SQL injection).
                await using var initCmd = pgConn.CreateCommand();
                initCmd.CommandText = PostgreSqlSessionInitializationSql;
                await initCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (options.ReadUncommitted && connection is SqlConnection)
            {
                // Pooled connections are reset to the default isolation level, so the setting is applied on every open.
                await using var isolationCmd = connection.CreateCommand();
                isolationCmd.CommandText = SqlServerReadUncommittedSql;
                await isolationCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (connection is SqliteConnection sqliteConn)
            {
                sqliteConn.CreateFunction("gateway_hmac_sha256", (string? val, string? salt) =>
                {
                    if (val == null) return null;
                    byte[] key = System.Text.Encoding.UTF8.GetBytes(salt ?? string.Empty);
                    byte[] data = System.Text.Encoding.UTF8.GetBytes(val);
                    byte[] hash = System.Security.Cryptography.HMACSHA256.HashData(key, data);
                    return "hmac_" + Convert.ToHexStringLower(hash);
                }, isDeterministic: true);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static NotSupportedException UnsupportedProvider(string? provider) =>
        new($"SQL provider '{provider}' is not supported. Supported providers are: 'Sqlite', 'SqlServer', 'PostgreSql'.");
}
