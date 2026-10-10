using System.Data.Common;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;

namespace Autheris.Infrastructure.Persistence;

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly bool _requireOracleTcps;
    private readonly IKeyVaultSecretProvider? _secrets;
    private readonly IHostEnvironment? _environment;

    public SqlConnectionFactory()
        : this(null, null)
    {
    }

    /// <summary>Oracle connections must use TCPS everywhere except in Development.</summary>
    public SqlConnectionFactory(IHostEnvironment? environment)
        : this(environment, null)
    {
    }

    /// <summary>
    /// Oracle connections must use TCPS and a Key Vault password everywhere except in Development
    /// (<see cref="DataSourceConnectionOptions.PasswordKeyVaultRef"/>, WP-F1).
    /// </summary>
    public SqlConnectionFactory(IHostEnvironment? environment, IKeyVaultSecretProvider? secrets)
    {
        _environment = environment;
        _secrets = secrets;
        _requireOracleTcps = environment is null || !environment.IsDevelopment();
    }

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

        string connectionString = options.ConnectionString;
        if (dialect == DatabaseDialect.Oracle)
        {
            // WP-F1: validated before any network traffic.
            OracleConnectionStringPolicy.Validate(connectionString, _requireOracleTcps);
            connectionString = ResolveOraclePassword(options, connectionString);
        }

        // Architecture 5: Databricks is a dialect without a driver here.
        DbConnection connection = dialect switch
        {
            DatabaseDialect.Sqlite => new SqliteConnection(options.ConnectionString),
            DatabaseDialect.SqlServer => new SqlConnection(options.ConnectionString),
            DatabaseDialect.PostgreSql => new Npgsql.NpgsqlConnection(options.ConnectionString),
            // CR-ADG-07: the raw driver connection never leaves this factory; every command is created with BindByName = true.
            DatabaseDialect.Oracle => new BindByNameOracleConnection(new OracleConnection(connectionString)),
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

            if (connection is BindByNameOracleConnection)
            {
                // WP-F2: pinned and verified on every pool rental, so one tenant's session state never reaches another.
                await using var pinCmd = connection.CreateCommand();
                pinCmd.CommandText = OracleSessionInitialization.PinAndVerifyBlock;
                await pinCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// CR-ADG-08: outside Development a plaintext password in the connection string is refused (fail closed); the password is
    /// resolved from the Key Vault reference and never logged.
    /// </summary>
    private string ResolveOraclePassword(DataSourceConnectionOptions options, string connectionString)
    {
        bool plaintext = OracleConnectionStringPolicy.HasPlaintextPassword(connectionString);
        if (_requireOracleTcps && plaintext)
        {
            throw new System.Security.SecurityException("An Oracle connection string with a plaintext password is not permitted outside Development; use PasswordKeyVaultRef.");
        }

        if (string.IsNullOrWhiteSpace(options.PasswordKeyVaultRef))
        {
            if (_requireOracleTcps)
            {
                throw new System.Security.SecurityException("An Oracle data source needs a Key Vault password reference outside Development.");
            }

            return connectionString;
        }

        string password = SecretReferenceResolver.Resolve(_secrets, options.PasswordKeyVaultRef, _environment, allowPlaintextInDevelopment: false, logger: null, "Oracle database password")
            ?? throw new System.Security.SecurityException("The Oracle database password could not be resolved (fail-closed).");
        return new OracleConnectionStringBuilder(connectionString) { Password = password }.ConnectionString;
    }

    private static NotSupportedException UnsupportedProvider(string? provider) =>
        new($"SQL provider '{provider}' is not supported. Supported providers are: 'Sqlite', 'SqlServer', 'PostgreSql'.");
}
