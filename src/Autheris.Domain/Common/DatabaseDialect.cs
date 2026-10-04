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
            throw new ArgumentException($"Ungültiger Bezeichner '{id}'. Erlaubt sind nur alphanumerische Zeichen und Unterstriche, beginnend mit einem Buchstaben oder Unterstrich.", paramName);
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

    public static DatabaseDialect ParseDialect(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return DatabaseDialect.PostgreSql;
        }

        return sourceType.Trim().ToLowerInvariant() switch
        {
            "mssql" or "sqlserver" or "sql_server" or "microsoft sql server" => DatabaseDialect.SqlServer,
            "sqlite" or "sqlite3" => DatabaseDialect.Sqlite,
            "postgres" or "postgresql" or "pgsql" or "npgsql" => DatabaseDialect.PostgreSql,
            "databricks" or "spark" or "sparksql" => DatabaseDialect.Databricks,
            "oracle" or "oracledb" or "odp" => DatabaseDialect.Oracle,
            _ => Enum.TryParse<DatabaseDialect>(sourceType, true, out var d) ? d : DatabaseDialect.PostgreSql
        };
    }

    public static string EscapeSqlLiteral(this DatabaseDialect dialect, string value)
    {
        if (value.Contains('\0'))
        {
            throw new InvalidOperationException("Literal enthält verbotenes Null-Byte (\\0). Potenzieller Injection-Angriff.");
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
                    throw new InvalidOperationException($"Ungültiger numerischer Literal-Wert im Zeilenfilter: {elem.GetRawText()}");
                }
                var raw = elem.GetRawText();
                if (!Regex.IsMatch(raw, @"^[+-]?[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?$"))
                {
                    throw new InvalidOperationException($"Ungültiges Zahlenformat im Zeilenfilter: '{raw}'");
                }
                return raw;

            case System.Text.Json.JsonValueKind.True:
                return (dialect is DatabaseDialect.SqlServer or DatabaseDialect.Oracle) ? "1" : "TRUE";

            case System.Text.Json.JsonValueKind.False:
                return (dialect is DatabaseDialect.SqlServer or DatabaseDialect.Oracle) ? "0" : "FALSE";

            case System.Text.Json.JsonValueKind.Null:
                return "NULL";

            default:
                throw new InvalidOperationException($"Nicht unterstützter Literal-Typ im Zeilenfilter: {elem.ValueKind}");
        }
    }
}
