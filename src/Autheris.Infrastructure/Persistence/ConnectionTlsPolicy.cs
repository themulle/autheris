using Autheris.Domain.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// DEP-7 / INF-6: outside Development every database connection must encrypt and verify the server certificate.
/// PostgreSQL requires <c>SSL Mode=VerifyCA</c> or <c>VerifyFull</c> (<c>Require</c> encrypts without checking the
/// certificate); SQL Server requires encryption and <c>TrustServerCertificate=false</c>. SQLite is a local file.
/// </summary>
public static class ConnectionTlsPolicy
{
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

        switch (dialect)
        {
            case DatabaseDialect.PostgreSql:
                try
                {
                    var pg = new NpgsqlConnectionStringBuilder(connectionString);
                    return pg.SslMode is SslMode.VerifyCA or SslMode.VerifyFull
                        ? null
                        : "PostgreSQL connections must use 'SSL Mode=VerifyFull' (or 'VerifyCA') outside Development.";
                }
                catch (ArgumentException)
                {
                    return "PostgreSQL connections must use 'SSL Mode=VerifyFull' (or 'VerifyCA') outside Development.";
                }

            case DatabaseDialect.SqlServer:
                try
                {
                    var sql = new SqlConnectionStringBuilder(connectionString);
                    if (sql.TrustServerCertificate)
                    {
                        return "SQL Server connections must not use 'TrustServerCertificate=true' outside Development.";
                    }

                    return sql.Encrypt == SqlConnectionEncryptOption.Optional
                        ? "SQL Server connections must use 'Encrypt=Mandatory' (or 'Strict') outside Development."
                        : null;
                }
                catch (ArgumentException)
                {
                    return "SQL Server connections must use 'Encrypt=Mandatory' (or 'Strict') outside Development.";
                }

            default:
                return null;
        }
    }

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

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Governance connection string must be configured outside Development.");
        }

        if (Validate(provider, connectionString) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }
    }
}
