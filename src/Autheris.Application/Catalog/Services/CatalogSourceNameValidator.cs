namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autheris.Domain.Model;

/// <summary>
/// SEC-SIMPL-143: Rejects reserved system schemas, system catalogs, and database dictionary views.
/// No catalog ingestion path can register a system schema, a system catalog or a dictionary view as a governed table.
/// </summary>
public static partial class CatalogSourceNameValidator
{
    private static readonly HashSet<string> ReservedSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "sys", "information_schema", "pg_catalog", "pg_toast", "system",
        "SYS", "SYSTEM", "XDB", "CTXSYS", "MDSYS"
    };

    [GeneratedRegex(@"^(ALL|USER|DBA|CDB)_(TABLES|TAB_COLUMNS|TAB_COLS|VIEWS|OBJECTS|CONSTRAINTS|CONS_COLUMNS|INDEXES|IND_COLUMNS|ARGUMENTS|PROCEDURES|SOURCE|SYNONYMS|TAB_STATISTICS|TAB_COL_STATISTICS|TAB_COMMENTS|COL_COMMENTS|DEPENDENCIES|POLICIES|SEGMENTS|TYPES|MVIEWS)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OracleDictRegex();

    [GeneratedRegex(@"^G?V\$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OracleVDollarRegex();

    public static bool IsReserved(string? domain, string? schema, string? table)
    {
        if (IsReservedCatalogOrSchema(domain) || IsReservedCatalogOrSchema(schema))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(table))
        {
            return false;
        }

        var t = table.Trim();
        if (t.StartsWith("pg_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ReservedSchemas.Contains(t))
        {
            return true;
        }

        if (string.Equals(t, "dbms_metadata", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (OracleDictRegex().IsMatch(t) || OracleVDollarRegex().IsMatch(t))
        {
            return true;
        }

        return false;
    }

    public static bool IsReservedCatalogOrSchema(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var s = name.Trim();
        return ReservedSchemas.Contains(s);
    }

    public static void Validate(TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (IsReserved(metadata.Identifier.Domain, metadata.Identifier.Schema, metadata.Identifier.TableName) ||
            IsReserved(metadata.Table.SourceName, metadata.Table.SchemaName, metadata.Table.TableName))
        {
            throw new CatalogReservedSourceNameException("The dataset references a reserved system schema, catalog or dictionary view name.");
        }
    }

    public static void Validate(string? domain, string? schema, string? table)
    {
        if (IsReserved(domain, schema, table))
        {
            throw new CatalogReservedSourceNameException("The dataset references a reserved system schema, catalog or dictionary view name.");
        }
    }
}

public sealed class CatalogReservedSourceNameException(string message) : ArgumentException(message)
{
    public const string ErrorCode = "CATALOG_RESERVED_SOURCE_NAME";
}
