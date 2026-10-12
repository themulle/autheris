namespace Autheris.Application.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;

public static class SqlDialectMapper
{
    public static bool TryToTargetDialect(DatabaseDialect dialect, out TargetSqlDialect target)
    {
        switch (dialect)
        {
            case DatabaseDialect.SqlServer:
                target = TargetSqlDialect.SqlServer;
                return true;
            case DatabaseDialect.PostgreSql:
                target = TargetSqlDialect.PostgreSql;
                return true;
            case DatabaseDialect.Sqlite:
                target = TargetSqlDialect.Sqlite;
                return true;
            case DatabaseDialect.Oracle:
                target = TargetSqlDialect.Oracle;
                return true;
            case DatabaseDialect.Databricks:
                target = TargetSqlDialect.Databricks;
                return true;
            default:
                target = default;
                return false;
        }
    }

    /// <summary>SEC-X1-14: never defaults. A dialect without a mapping throws, so a new enum value cannot be mapped silently.</summary>
    public static TargetSqlDialect ToTargetDialect(DatabaseDialect dialect) =>
        TryToTargetDialect(dialect, out var target)
            ? target
            : throw new NotSupportedException($"Target AST generation not supported for dialect '{dialect}'.");

    /// <summary>
    /// Whether <c>SqlConnectionFactory</c> can open a connection for the dialect (including the Databricks REST facade). The factory
    /// throws for every other value; <c>SqlDialectConsistencyTests</c> pins this list against it.
    /// </summary>
    public static bool ConnectionFactorySupports(DatabaseDialect dialect) =>
        dialect is DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite or DatabaseDialect.Oracle or DatabaseDialect.Databricks;

    /// <summary>
    /// X1.3, WebSQL admission: a dialect is executable when it maps to an AST dialect, the connection factory supports it and its
    /// capability entry has the tier <see cref="DialectSupportTier.Production"/>. A tier flip therefore admits a dialect, which is why
    /// <c>DialectCapabilityTable.cs</c> is under security CODEOWNERS and the admitted set is pinned by <c>Admission_ExactSet</c>.
    /// Databricks maps and connects but stays Experimental until X1-DBX (S-4); DuckDb, Snowflake and Ansi are not database dialects.
    /// </summary>
    public static bool IsExecutable(DatabaseDialect dialect) => IsExecutable(dialect, DialectCapabilityTable.Default);

    /// <summary>The admission rule against an explicit capability source (tests prove it is derived from the tier).</summary>
    public static bool IsExecutable(DatabaseDialect dialect, IDialectCapabilityProvider capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!TryToTargetDialect(dialect, out var target) || !ConnectionFactorySupports(dialect))
        {
            return false;
        }

        try
        {
            return capabilities.Get(target).Tier == DialectSupportTier.Production;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;   // no capability entry: never admitted
        }
    }

    /// <summary>The admitted dialects in a stable order, derived from the capability table.</summary>
    public static IReadOnlyList<DatabaseDialect> AdmittedDialects { get; } =
        Enum.GetValues<DatabaseDialect>().Where(static d => IsExecutable(d)).OrderBy(static d => DisplayName(d), StringComparer.Ordinal).ToArray();

    /// <summary>The message of every WebSQL rejection that is caused by the dialect; the list comes from the table, never from a literal.</summary>
    public static string UnsupportedDialectMessage { get; } =
        $"WebSQL only supports tables of {List(AdmittedDialects)} data sources.";

    /// <summary>The message for a data source whose provider is not admitted.</summary>
    public static string UnsupportedProviderMessage { get; } =
        $"The data source uses a database provider that is not supported by WebSQL (supported: {List(AdmittedDialects)}).";

    public static string DisplayName(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.PostgreSql => "PostgreSQL",
        DatabaseDialect.SqlServer => "SQL Server",
        DatabaseDialect.Sqlite => "SQLite",
        DatabaseDialect.Oracle => "Oracle",
        DatabaseDialect.Databricks => "Databricks",
        _ => dialect.ToString()
    };

    private static string List(IReadOnlyList<DatabaseDialect> dialects)
    {
        var names = dialects.Select(DisplayName).ToArray();
        return names.Length switch
        {
            0 => "no",
            1 => names[0],
            _ => string.Join(", ", names[..^1]) + " and " + names[^1]
        };
    }
}
