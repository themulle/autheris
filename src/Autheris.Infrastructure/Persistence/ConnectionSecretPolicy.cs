namespace Autheris.Infrastructure.Persistence;

using System;
using System.Security;
using Autheris.Domain.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

public static class ConnectionSecretPolicy
{
    public static bool HasPlaintextPassword(DatabaseDialect dialect, string connectionString, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;
        try
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => !string.IsNullOrEmpty(new NpgsqlConnectionStringBuilder(connectionString).Password),
                DatabaseDialect.SqlServer => !string.IsNullOrEmpty(new SqlConnectionStringBuilder(connectionString).Password),
                DatabaseDialect.Oracle => !string.IsNullOrEmpty(new OracleConnectionStringBuilder(connectionString).Password),
                _ => false
            };
        }
        catch (Exception ex) when (ex is not SecurityException)
        {
            logger?.LogWarning(
                "Failed to parse {Dialect} connection string for secret policy evaluation ({ExceptionType}).",
                dialect,
                ex.GetType().Name);
            throw new SecurityException("Failed to parse connection string for secret policy evaluation.");
        }
    }

    public static string WithPassword(DatabaseDialect dialect, string connectionString, string password)
    {
        return dialect switch
        {
            DatabaseDialect.PostgreSql => new NpgsqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString,
            DatabaseDialect.SqlServer => new SqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString,
            DatabaseDialect.Oracle => new OracleConnectionStringBuilder(connectionString) { Password = password }.ConnectionString,
            _ => connectionString
        };
    }

    public static bool AppliesTo(DatabaseDialect d) => d is DatabaseDialect.PostgreSql or DatabaseDialect.SqlServer or DatabaseDialect.Oracle;
}
