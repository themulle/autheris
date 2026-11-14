using Autheris.Domain.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// DEP-7 / INF-6: outside Development every database connection must encrypt and verify the server certificate.
/// PostgreSQL requires <c>SSL Mode=VerifyFull</c> (<c>Require</c> and <c>VerifyCA</c> do not verify the server hostname);
/// SQL Server requires encryption and <c>TrustServerCertificate=false</c>; Oracle requires TCPS. SQLite is a local file.
/// </summary>
public static class ConnectionTlsPolicy
{
    private const string PostgreSqlVerifyFullRequiredMessage =
        "PostgreSQL connections must use 'SSL Mode=VerifyFull' outside Development.";

    private const string PostgreSqlTrustServerCertificateRejectedMessage =
        "PostgreSQL connections must not use 'TrustServerCertificate=true' outside Development.";

    private const string SqlServerEncryptRequiredMessage =
        "SQL Server connections must use 'Encrypt=Mandatory' (or 'Strict') outside Development.";

    private const string SqlServerTrustServerCertificateRejectedMessage =
        "SQL Server connections must not use 'TrustServerCertificate=true' outside Development.";

    /// <summary>Returns null when the connection string satisfies the policy, otherwise the reason.</summary>
    public static string? Validate(string? provider, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        // Architecture 5: same provider aliases as the connection factory; an unknown provider is not silently passed.
        if (!DataSourceProvider.TryResolveDialect(provider, out var dialect))
        {
            return $"Unknown database provider '{provider}'.";
        }

        return ValidateDialect(dialect, connectionString);
    }

    internal static string? ValidateDialect(DatabaseDialect dialect, string connectionString) => dialect switch
    {
        DatabaseDialect.PostgreSql => ValidatePostgreSql(connectionString),
        DatabaseDialect.SqlServer => ValidateSqlServer(connectionString),
        DatabaseDialect.Oracle => OracleConnectionStringPolicy.ValidateTcps(connectionString),
        // Databricks has no ADO.NET driver or connection string; every statement goes through the hardened HTTPS REST client.
        DatabaseDialect.Databricks => null,
        // SQLite is a local in-process database file (data-source dialect only); there is no network transport to encrypt.
        DatabaseDialect.Sqlite => null,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, $"Unsupported database dialect '{dialect}'.")
    };

    private static string? ValidatePostgreSql(string connectionString)
    {
        try
        {
            var pg = new NpgsqlConnectionStringBuilder(connectionString);
            if (pg.TryGetValue("Trust Server Certificate", out var tscObj) && IsTrueValue(tscObj))
            {
                return PostgreSqlTrustServerCertificateRejectedMessage;
            }

            return pg.SslMode == SslMode.VerifyFull
                ? null
                : PostgreSqlVerifyFullRequiredMessage;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return PostgreSqlVerifyFullRequiredMessage;
        }
    }

    private static string? ValidateSqlServer(string connectionString)
    {
        try
        {
            var sql = new SqlConnectionStringBuilder(connectionString);
            if (sql.TrustServerCertificate)
            {
                return SqlServerTrustServerCertificateRejectedMessage;
            }

            return sql.Encrypt == SqlConnectionEncryptOption.Optional
                ? SqlServerEncryptRequiredMessage
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return SqlServerEncryptRequiredMessage;
        }
    }

    private static bool IsTrueValue(object? value) =>
        value is true || (value is string s && bool.TryParse(s.Trim(), out var parsed) && parsed);

    /// <summary>
    /// Validates TLS settings for a governance database connection outside Development.
    /// Throws <see cref="InvalidOperationException"/> naming only the required setting without echoing the connection string.
    /// </summary>
    public static void ValidateGovernanceConnectionTls(string? provider, string? connectionString, bool isDevelopment = false)
    {
        if (isDevelopment)
        {
            return;
        }

        string effectiveProvider = string.IsNullOrWhiteSpace(provider) ? "PostgreSql" : provider.Trim();
        if (!DataSourceProvider.TryResolveDialect(effectiveProvider, out var dialect) ||
            dialect is not (DatabaseDialect.PostgreSql or DatabaseDialect.SqlServer))
        {
            throw new InvalidOperationException("Governance database provider must be PostgreSql or SqlServer.");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Governance connection string must be configured outside Development.");
        }

        if (ValidateDialect(dialect, connectionString) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }
    }
}
