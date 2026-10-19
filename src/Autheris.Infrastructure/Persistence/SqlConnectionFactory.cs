using System.Data.Common;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace Autheris.Infrastructure.Persistence;

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly bool _requireOracleTcps;
    private readonly IKeyVaultSecretProvider? _secrets;
    private readonly IHostEnvironment? _environment;
    private readonly IServiceProvider? _services;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;
    private readonly SqlLoginCircuitBreaker _loginBreaker;

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
    /// (<see cref="DataSourceConnectionOptions.PasswordKeyVaultRef"/>, WP-F1). <paramref name="services"/> resolves the Databricks REST
    /// client (WP-X0j); without it a Databricks data source is refused (fail closed).
    /// </summary>
    public SqlConnectionFactory(IHostEnvironment? environment, IKeyVaultSecretProvider? secrets, IServiceProvider? services = null, TimeProvider? timeProvider = null)
    {
        _environment = environment;
        _secrets = secrets;
        _services = services;
        _logger = services?.GetService<Microsoft.Extensions.Logging.ILogger<SqlConnectionFactory>>();
        _loginBreaker = new SqlLoginCircuitBreaker(timeProvider, _logger);
        _requireOracleTcps = environment is null || !environment.IsDevelopment();
    }

    /// <summary>RR-L5-01: Session settings required by the literal escaping in <c>DatabaseDialect.EscapeSqlLiteral</c>.</summary>
    public const string PostgreSqlSessionInitializationSql = "SET standard_conforming_strings = on";

    /// <summary>WP-X0h (SEC-X1-10): executed on every SQLite connection right after it is opened.</summary>
    public const string SqliteTrustedSchemaOffSql = "PRAGMA trusted_schema = OFF";

    /// <summary>Dirty read for SQL Server sessions of data sources with <c>ReadUncommitted</c> (reads take no shared locks).</summary>
    public const string SqlServerReadUncommittedSql = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED";

    public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (DataSourceProvider.Is(options.Provider, DatabaseDialect.Databricks))
        {
            // WP-X0j: no ADO.NET driver and no connection string; the facade sends every statement through the hardened REST client.
            return await CreateDatabricksConnectionAsync(options, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException("Connection string cannot be empty for SQL data source.", nameof(options));
        }

        if (!DataSourceProvider.TryResolveDialect(options.Provider, out var dialect))
        {
            throw UnsupportedProvider(options.Provider);
        }

        // WP-X0b (SEC-X1-08): checked on every rental and before any network traffic, so a reloaded or incomplete data source never
        // connects unpinned. A present value must always be valid, even when the source does not require one.
        bool schemaPinned = dialect is DatabaseDialect.PostgreSql or DatabaseDialect.Oracle;
        if (schemaPinned && options.DefaultSchema is not null && !DefaultSchemaPolicy.IsValid(options.DefaultSchema))
        {
            throw new SqlSourceUnavailableException("The data source default schema is invalid.");
        }

        if (schemaPinned && options.RequireDefaultSchema && string.IsNullOrWhiteSpace(options.DefaultSchema))
        {
            throw new SqlSourceUnavailableException("The data source requires a default schema and has none.");
        }

        string connectionString = options.ConnectionString;
        SspiContextProvider? sqlServerSspiProvider = null;
        if (dialect == DatabaseDialect.Oracle)
        {
            // WP-F1: validated before any network traffic.
            OracleConnectionStringPolicy.Validate(connectionString, _requireOracleTcps);
            connectionString = ResolveOraclePassword(options, connectionString);
        }
        else if (dialect == DatabaseDialect.SqlServer)
        {
            // Lockout protection: while the login breaker is open nothing is resolved and the server is not contacted (fail closed).
            _loginBreaker.ThrowIfOpen(options);
            // Issue #102: validate and configure Managed NTLM (System.Net.Security.UseManagedNtlm) / GSSAPI (gss-ntlmssp / libgssapi-krb5-2) before opening.
            connectionString = SqlServerWindowsAuthConfigurator.PrepareConnection(options, _secrets, _environment, out sqlServerSspiProvider);
            LogAuthPathOnce(options);
        }

        DbConnection connection = dialect switch
        {
            DatabaseDialect.Sqlite => new SqliteConnection(options.ConnectionString),
            DatabaseDialect.SqlServer => CreateSqlServerConnection(connectionString, sqlServerSspiProvider),
            DatabaseDialect.PostgreSql => new Npgsql.NpgsqlConnection(options.ConnectionString),
            // CR-ADG-07: the raw driver connection never leaves this factory; every command is created with BindByName = true.
            DatabaseDialect.Oracle => new BindByNameOracleConnection(new OracleConnection(connectionString)),
            _ => throw UnsupportedProvider(options.Provider)
        };

        try
        {
            try
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
            }
            catch (Exception openEx) when (dialect == DatabaseDialect.SqlServer)
            {
                var managedProvider = sqlServerSspiProvider as ManagedNtlmSspiContextProvider;
                var classified = SqlServerWindowsAuthConfigurator.ClassifyOpenException(
                    openEx, options, managedNtlmUsed: managedProvider is not null, managedNtlmFailure: managedProvider?.LastFailure);
                _loginBreaker.RecordFailure(options, classified);
                throw classified;
            }

            if (dialect == DatabaseDialect.SqlServer)
            {
                _loginBreaker.RecordSuccess(options);
            }

            if (connection is Npgsql.NpgsqlConnection pgConn)
            {
                // RR-L5-01: Row-filter literals are escaped for standard_conforming_strings = on (backslashes are
                // literal). Enforce that mode on every session instead of relying on the server default; with "off"
                // a trailing backslash would escape the closing quote (SQL injection).
                await using var initCmd = pgConn.CreateCommand();
                initCmd.CommandText = PostgreSqlSessionInitializationSql;
                await initCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                if (options.DefaultSchema is not null)
                {
                    // WP-X0b: search_path = pg_catalog, <schema>, pg_temp; the schema is a bound value quoted by format('%I').
                    await using var pinCmd = pgConn.CreateCommand();
                    pinCmd.CommandText = SessionPinning.PostgreSqlSearchPathPinSql;
                    AddParameter(pinCmd, "@schema", options.DefaultSchema);
                    SessionPinning.EnsurePinned(await pinCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as bool?, "search_path");
                }
            }

            if (connection is BindByNameOracleConnection)
            {
                // WP-F2: pinned and verified on every pool rental, so one tenant's session state never reaches another.
                await using var pinCmd = connection.CreateCommand();
                pinCmd.CommandText = OracleSessionInitialization.PinAndVerifyBlock;
                await pinCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                if (options.DefaultSchema is not null)
                {
                    // WP-X0b: the schema is bound; DBMS_ASSERT proves existence and quotes it inside the database.
                    await using var schemaCmd = connection.CreateCommand();
                    schemaCmd.CommandText = SessionPinning.OracleSchemaPinBlock;
                    AddParameter(schemaCmd, "schema", options.DefaultSchema);
                    await schemaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            if (connection is SqlConnection)
            {
                // WP-X0b: ANSI options are pinned on every rental; pooled sessions are reset to the login defaults.
                await using var ansiCmd = connection.CreateCommand();
                ansiCmd.CommandText = SessionPinning.SqlServerAnsiPinSql;
                await ansiCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                await using var verifyCmd = connection.CreateCommand();
                verifyCmd.CommandText = SessionPinning.SqlServerAnsiVerifySql;
                SessionPinning.EnsurePinned(Convert.ToInt32(await verifyCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1, "ansi_options");
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
                // WP-X0h (SEC-X1-10): extension loading stays off and the schema is untrusted, so a view, trigger or default in the
                // database cannot call a function that is not marked innocuous.
                sqliteConn.EnableExtensions(false);
                await using var hardenCmd = connection.CreateCommand();
                hardenCmd.CommandText = SqliteTrustedSchemaOffSql;
                await hardenCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                // Phase 5 L8: read back, never assume. A pragma that did not take effect fails the rental closed.
                await using var verifyTrustCmd = connection.CreateCommand();
                verifyTrustCmd.CommandText = SessionPinning.SqliteTrustedSchemaReadBackSql;
                SessionPinning.EnsureSqliteTrustedSchemaOff(await verifyTrustCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

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

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataSourceConnectionOptions, object> LoggedAuthPaths = new();

    /// <summary>Logs the SQL Server authentication path once per data source configuration (no secrets).</summary>
    private void LogAuthPathOnce(DataSourceConnectionOptions options)
    {
        if (_logger is null || !LoggedAuthPaths.TryAdd(options, new object()))
        {
            return;
        }

        _logger.LogInformation(
            "SQL Server data source authentication path: {AuthPath} (managed NTLM setting: {UseManagedNtlm}).",
            SqlServerWindowsAuthConfigurator.DescribeAuthPath(options),
            ManagedNtlmPolicy.Resolve(options.WindowsAuth?.UseManagedNtlm));
    }

    private async Task<DbConnection> CreateDatabricksConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct)
    {
        var settings = options.Databricks
            ?? throw new ArgumentException("A Databricks data source requires its Databricks section.", nameof(options));
        if (!DatabricksHostPolicy.IsValidHost(settings.Host))
        {
            throw new ArgumentException("The Databricks host must be one exact DNS name without scheme, port, path or wildcard.", nameof(options));
        }

        var client = _services?.GetService<DatabricksStatementExecutionClient>()
            ?? throw new NotSupportedException("The Databricks REST client is not registered; a Databricks data source cannot be opened.");

        // The client resolves the section by data source name; the options instance is the configured one, so find its name by identity.
        string? dataSourceName = null;
        if (_services?.GetService<IOptions<GatewayOptions>>()?.Value.DataSources.Connections is { } connections)
        {
            foreach (var (name, candidate) in connections)
            {
                if (ReferenceEquals(candidate, options))
                {
                    dataSourceName = name;
                    break;
                }
            }
        }

        var connection = new DatabricksDbConnection(client, settings, dataSourceName);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
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

    private static SqlConnection CreateSqlServerConnection(string connectionString, SspiContextProvider? sspiContextProvider)
    {
        var sqlConnection = new SqlConnection(connectionString);
        if (sspiContextProvider is not null)
        {
            sqlConnection.SspiContextProvider = sspiContextProvider;
        }

        return sqlConnection;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static NotSupportedException UnsupportedProvider(string? provider) =>
        new($"SQL provider '{provider}' is not supported. Supported providers are: 'Sqlite', 'SqlServer', 'PostgreSql', 'Oracle', 'Databricks'.");

    /// <summary>
    /// Issue #132 (PLAN-DATA-132, SEC-DATA132-05): Verifies physical SQL Server instance identity
    /// via SELECT @@SERVERNAME. Returns the runtime server name string.
    /// </summary>
    public static async Task<string?> VerifyServerIdentityAsync(DbConnection connection, string? expectedHost = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT @@SERVERNAME";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string ?? result?.ToString();
    }
}
