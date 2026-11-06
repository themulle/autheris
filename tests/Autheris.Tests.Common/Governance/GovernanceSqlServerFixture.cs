using System;
using System.Text;
using System.Threading.Tasks;

namespace Autheris.Tests.Common.Governance;

/// <summary>
/// Shared SQL Server governance store fixture contract for integration tests.
/// Starts one SQL Server 2022 container per test process (or connects to <c>AUTHERIS_TEST_MSSQL</c>)
/// and provisions isolated databases migrated with <c>SqlServerGovernanceMigrationRunner</c>.
/// </summary>
public interface IGovernanceSqlServerFixture
{
    /// <summary>True when a SQL Server instance is reachable and ready to create migrated governance databases.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Creates a fresh database migrated with the governance schema and returns its connection string.
    /// </summary>
    Task<string> CreateDatabaseAsync(string? name = null);
}

public static class GovernanceSqlServerDatabaseName
{
    public static string Sanitize(string? prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return "autheris_" + suffix;
        }

        var sb = new StringBuilder("autheris_", 63);
        foreach (var ch in prefix)
        {
            if (sb.Length >= 48)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb[^1] != '_')
            {
                sb.Append('_');
            }
        }

        if (sb[^1] != '_')
        {
            sb.Append('_');
        }

        sb.Append(suffix);
        return sb.ToString();
    }
}
