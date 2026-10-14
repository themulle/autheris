using System.Text.RegularExpressions;

namespace Autheris.Domain.Common;

public enum DatabaseDialect
{
    PostgreSql = 1,
    SqlServer = 2,
    Sqlite = 3,
    Databricks = 4,
    Oracle = 5
}

public static class DatabaseDialectExtensions
{
    private static readonly Regex SafeIdentifierRegex =
        new(@"\A[a-zA-Z_][a-zA-Z0-9_]*\z", RegexOptions.Compiled);

    public static void ValidateIdentifier(string id, string paramName = "Identifier")
    {
        if (string.IsNullOrWhiteSpace(id) || !SafeIdentifierRegex.IsMatch(id))
        {
            throw new ArgumentException($"Invalid identifier '{id}'. Only alphanumeric characters and underscores are allowed, starting with a letter or underscore.", paramName);
        }
    }

    public static string QuoteIdentifier(this DatabaseDialect dialect, string identifier)
    {
        ValidateIdentifier(identifier);
        return dialect switch
        {
            DatabaseDialect.SqlServer => $"[{identifier}]",
            DatabaseDialect.Databricks => $"`{identifier}`",
            DatabaseDialect.Oracle or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite => $"\"{identifier}\"",
            _ => $"\"{identifier}\""
        };
    }

    /// <summary>
    /// Parameter marker in SQL text. Oracle uses <c>:name</c>; the other dialects use <c>@name</c>. Hard-coded <c>@</c> markers
    /// are forbidden on governed paths (SEC-ADG-03): ODP.NET would not even recognize them.
    /// </summary>
    public static string FormatParameterMarker(this DatabaseDialect dialect, string name) =>
        dialect == DatabaseDialect.Oracle ? ":" + name : "@" + name;

    /// <summary>Provider-side parameter name matching <see cref="FormatParameterMarker"/> (Oracle has no prefix).</summary>
    public static string FormatParameterName(this DatabaseDialect dialect, string name) =>
        dialect == DatabaseDialect.Oracle ? name : "@" + name;

    public static string QuoteQualifiedColumn(this DatabaseDialect dialect, string qualifiedColumn)
    {
        if (string.IsNullOrWhiteSpace(qualifiedColumn))
        {
            throw new ArgumentException("Qualified column cannot be empty.", nameof(qualifiedColumn));
        }

        var parts = qualifiedColumn.Split('.');
        foreach (var part in parts)
        {
            ValidateIdentifier(part);
        }

        return string.Join(".", parts.Select(p => dialect.QuoteIdentifier(p)));
    }

    public static string FormatTableIdentifier(this DatabaseDialect dialect, TableIdentifier table)
    {
        ValidateIdentifier(table.Schema, "Schema");
        ValidateIdentifier(table.TableName, "TableName");

        var schemaQuoted = dialect.QuoteIdentifier(table.Schema);
        var tableQuoted = dialect.QuoteIdentifier(table.TableName);

        if (dialect == DatabaseDialect.Sqlite)
        {
            return tableQuoted;
        }

        return $"{schemaQuoted}.{tableQuoted}";
    }

    /// <summary>
    /// D-1: Maps a catalog source type or connection provider to a dialect. Fail-closed: empty, unknown and undefined
    /// numeric values throw instead of falling back to a default dialect (the dialect decides quoting and filter syntax).
    /// </summary>
    public static DatabaseDialect ParseDialect(string? sourceType) =>
        TryParseDialect(sourceType, out var dialect)
            ? dialect
            : throw new NotSupportedException($"Unsupported database dialect/source type: '{sourceType}'.");

    public static bool TryParseDialect(string? sourceType, out DatabaseDialect dialect)
    {
        dialect = default;
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return false;
        }

        var normalized = sourceType.Trim().ToLowerInvariant();
        DatabaseDialect? mapped = normalized switch
        {
            "mssql" or "sqlserver" or "sql_server" or "microsoft sql server" => DatabaseDialect.SqlServer,
            "sqlite" or "sqlite3" => DatabaseDialect.Sqlite,
            "postgres" or "postgresql" or "pgsql" or "npgsql" => DatabaseDialect.PostgreSql,
            "databricks" or "spark" or "sparksql" => DatabaseDialect.Databricks,
            "oracle" or "oracledb" or "odp" => DatabaseDialect.Oracle,
            _ => null
        };
        if (mapped.HasValue)
        {
            dialect = mapped.Value;
            return true;
        }

        // Enum names or numbers, but no flag combinations ("SqlServer, Sqlite") and no undefined numbers ("99").
        if (!normalized.Contains(',') &&
            Enum.TryParse<DatabaseDialect>(normalized, true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            dialect = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// D-1: Source type to store for a table imported from an external catalog (Alation, Collibra, Purview, OpenMetadata).
    /// A supported dialect already on the table is kept (a catalog sync never switches the dialect); otherwise the catalog
    /// value is used. <paramref name="isSupported"/> is false when the result is not a database dialect, so the table stays
    /// unqueryable (fail-closed) until an administrator sets the source type.
    /// </summary>
    public static string ResolveCatalogSourceType(string? catalogSourceType, string? existingSourceType, out bool isSupported)
    {
        if (TryParseDialect(existingSourceType, out _))
        {
            isSupported = true;
            return existingSourceType!.Trim();
        }

        isSupported = TryParseDialect(catalogSourceType, out _);
        return catalogSourceType?.Trim() ?? existingSourceType?.Trim() ?? string.Empty;
    }

    public static string EscapeSqlLiteral(this DatabaseDialect dialect, string value)
    {
        if (value.Contains('\0'))
        {
            throw new InvalidOperationException("Literal contains a forbidden null byte (\\0). Potential injection attack.");
        }

        // Standard single quote doubling
        var escaped = value.Replace("'", "''");

        // Dialect-specific backslash protection:
        // In Databricks (Spark SQL), backslashes are escape characters in string literals.
        // In PostgreSQL (with standard_conforming_strings = on, default since 9.1),
        // backslashes in standard '...' literals are literal characters and must not be doubled.
        if (dialect is DatabaseDialect.Databricks)
        {
            escaped = escaped.Replace(@"\", @"\\");
        }

        return escaped;
    }

    public static string FormatSafeLiteral(this DatabaseDialect dialect, System.Text.Json.JsonElement elem)
    {
        switch (elem.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                var str = elem.GetString() ?? string.Empty;
                var escaped = dialect.EscapeSqlLiteral(str);
                return $"'{escaped}'";

            case System.Text.Json.JsonValueKind.Number:
                if (!elem.TryGetInt64(out _) && !elem.TryGetDecimal(out _))
                {
                    throw new InvalidOperationException($"Invalid numeric literal value in row filter: {elem.GetRawText()}");
                }
                var raw = elem.GetRawText();
                if (!Regex.IsMatch(raw, @"^[+-]?[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?$"))
                {
                    throw new InvalidOperationException($"Invalid number format in row filter: '{raw}'");
                }
                return raw;

            case System.Text.Json.JsonValueKind.True:
                return (dialect is DatabaseDialect.SqlServer or DatabaseDialect.Oracle) ? "1" : "TRUE";

            case System.Text.Json.JsonValueKind.False:
                return (dialect is DatabaseDialect.SqlServer or DatabaseDialect.Oracle) ? "0" : "FALSE";

            case System.Text.Json.JsonValueKind.Null:
                return "NULL";

            default:
                throw new InvalidOperationException($"Unsupported literal type in row filter: {elem.ValueKind}");
        }
    }
}

/// <summary>
/// Architecture 5 / SQL2-22: the one mapping from a data source provider name (<c>DataSources:Connections:*:Provider</c>,
/// <c>GovernanceDb:Provider</c>) to a dialect. The connection factory, the TLS policy, WebSQL, procedures and the tree
/// path all resolve through here, so an alias is either accepted everywhere or nowhere. An empty provider means SQLite,
/// the option default.
/// </summary>
public static class DataSourceProvider
{
    public static bool TryResolveDialect(string? provider, out DatabaseDialect dialect) =>
        DatabaseDialectExtensions.TryParseDialect(string.IsNullOrWhiteSpace(provider) ? "sqlite" : provider, out dialect);

    public static bool Is(string? provider, DatabaseDialect expected) =>
        TryResolveDialect(provider, out var dialect) && dialect == expected;
}
