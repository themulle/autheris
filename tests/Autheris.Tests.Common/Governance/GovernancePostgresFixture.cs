namespace Autheris.Tests.Common.Governance;

using System;
using System.Text;
using System.Threading.Tasks;

/// <summary>
/// Shared contract and identifier sanitization helpers for template-cloned PostgreSQL governance test databases.
/// </summary>
public interface IGovernancePostgresFixture
{
    bool IsAvailable { get; }
    string TemplateDatabaseName { get; }
    Task<string> CreateDatabaseAsync(string? name = null);
}

public static class GovernancePostgresDatabaseName
{
    public const string DefaultTemplateName = "autheris_template";

    public static string Sanitize(string? name)
    {
        var raw = string.IsNullOrWhiteSpace(name)
            ? "gov_" + Guid.NewGuid().ToString("N")[..16]
            : name.Trim();

        var sb = new StringBuilder(raw.Length + 16);
        foreach (var ch in raw)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                sb.Append('_');
            }
        }

        if (sb.Length == 0 || !char.IsAsciiLetter(sb[0]))
        {
            sb.Insert(0, "db_");
        }

        var suffix = "_" + Guid.NewGuid().ToString("N")[..8];
        const int maxIdentifierLength = 63;
        if (sb.Length + suffix.Length > maxIdentifierLength)
        {
            sb.Length = maxIdentifierLength - suffix.Length;
        }

        sb.Append(suffix);
        return sb.ToString();
    }
}
