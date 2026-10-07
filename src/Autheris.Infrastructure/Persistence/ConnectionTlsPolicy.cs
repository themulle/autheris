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

        switch (provider?.Trim().ToLowerInvariant())
        {
            case "postgres" or "postgresql" or "npgsql":
                var pg = new NpgsqlConnectionStringBuilder(connectionString);
                return pg.SslMode is SslMode.VerifyCA or SslMode.VerifyFull
                    ? null
                    : "PostgreSQL connections must use 'SSL Mode=VerifyFull' (or 'VerifyCA') outside Development.";

            case "sqlserver" or "mssql" or "microsoft sql server":
                var sql = new SqlConnectionStringBuilder(connectionString);
                if (sql.TrustServerCertificate)
                {
                    return "SQL Server connections must not use 'TrustServerCertificate=true' outside Development.";
                }

                return sql.Encrypt == SqlConnectionEncryptOption.Optional
                    ? "SQL Server connections must use 'Encrypt=Mandatory' (or 'Strict') outside Development."
                    : null;

            default:
                return null;
        }
    }
}
