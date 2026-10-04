namespace Autheris.Domain.Common;

using System;

/// <summary>
/// Architecture Phase 2 (WP 2.1): Canonical TableIdentifier & Case-Folding Normalizer.
/// Eliminates RR-L3-06 (Arrow raw table injection) and RR-L5-02 (PostgreSQL / Oracle case sensitivity mismatches).
/// </summary>
public static class TableIdentifierNormalizer
{
    public const string DefaultDomain = "default";
    public const string DefaultSchema = "public";

    /// <summary>
    /// Parses a raw string identifier (1-part "table", 2-part "schema.table", or 3-part "domain.schema.table"),
    /// applies dialect-specific case-folding, and validates safe identifier syntax.
    /// </summary>
    public static TableIdentifier Normalize(
        string rawIdentifier,
        string defaultDomain = DefaultDomain,
        string defaultSchema = DefaultSchema,
        DatabaseDialect dialect = DatabaseDialect.PostgreSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawIdentifier);

        var trimmed = rawIdentifier.Trim();
        var parts = trimmed.Split('.');

        string domain;
        string schema;
        string table;

        if (parts.Length == 1)
        {
            domain = string.IsNullOrWhiteSpace(defaultDomain) ? DefaultDomain : defaultDomain.Trim();
            schema = string.IsNullOrWhiteSpace(defaultSchema) ? DefaultSchema : defaultSchema.Trim();
            table = parts[0].Trim();
        }
        else if (parts.Length == 2)
        {
            domain = string.IsNullOrWhiteSpace(defaultDomain) ? DefaultDomain : defaultDomain.Trim();
            schema = parts[0].Trim();
            table = parts[1].Trim();
        }
        else if (parts.Length == 3)
        {
            domain = parts[0].Trim();
            schema = parts[1].Trim();
            table = parts[2].Trim();
        }
        else
        {
            throw new ArgumentException($"Invalid table identifier '{rawIdentifier}'. Expected 1 to 3 dot-separated parts.", nameof(rawIdentifier));
        }

        // Apply dialect-specific case folding for unquoted identifiers
        domain = FoldCase(domain, dialect);
        schema = FoldCase(schema, dialect);
        table = FoldCase(table, dialect);

        // Validate safe identifier characters
        DatabaseDialectExtensions.ValidateIdentifier(schema, nameof(schema));
        DatabaseDialectExtensions.ValidateIdentifier(table, nameof(table));

        return new TableIdentifier(domain, schema, table);
    }

    /// <summary>
    /// Attempts to parse and normalize the raw identifier. Returns false if the input is invalid.
    /// </summary>
    public static bool TryNormalize(
        string? rawIdentifier,
        out TableIdentifier identifier,
        string defaultDomain = DefaultDomain,
        string defaultSchema = DefaultSchema,
        DatabaseDialect dialect = DatabaseDialect.PostgreSql)
    {
        identifier = default;
        if (string.IsNullOrWhiteSpace(rawIdentifier))
        {
            return false;
        }

        try
        {
            identifier = Normalize(rawIdentifier, defaultDomain, defaultSchema, dialect);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies dialect-specific case folding rules.
    /// PostgreSQL/Sqlite/Databricks unquoted identifiers fold to lower case.
    /// Oracle unquoted identifiers fold to upper case.
    /// SQL Server preserves case.
    /// </summary>
    public static string FoldCase(string identifier, DatabaseDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return identifier;
        }

        return dialect switch
        {
            DatabaseDialect.PostgreSql => identifier.ToLowerInvariant(),
            DatabaseDialect.Sqlite => identifier.ToLowerInvariant(),
            DatabaseDialect.Databricks => identifier.ToLowerInvariant(),
            DatabaseDialect.Oracle => identifier.ToUpperInvariant(),
            DatabaseDialect.SqlServer => identifier,
            _ => identifier
        };
    }
}
