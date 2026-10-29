namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Which rule produced a default read ordering. Internal diagnostics only (logs, telemetry): it is never written to a
/// response body, header or annotation, because it would tell a caller that a key contains a column hidden from it.
/// </summary>
public enum DefaultOrderingKind
{
    /// <summary>The caller named the ordering.</summary>
    Explicit = 0,
    PrimaryKey = 1,
    UniqueIndex = 2,
    /// <summary>Column <c>Meta</c> primary key markers (validated like a declared key).</summary>
    MetaMarkerKey = 3,
    /// <summary>The <c>(client_id, ts)</c> convention; only when both columns exist and are readable in clear text.</summary>
    ConventionKey = 4,
    ClearColumnComposite = 5,
    None = 6
}

/// <summary>The columns to order by (catalog spelling) and the rule that chose them.</summary>
public sealed record DefaultOrdering(IReadOnlyList<string> Columns, DefaultOrderingKind Kind);

/// <summary>
/// Issue #94 (D-10, SD-1, SD-2): the single default ordering rule of every read path (table-read planner, GraphQL tree
/// compiler, cross-source reads). Precedence: declared primary key, declared unique index (fewest columns), Meta marker key,
/// the <c>(client_id, ts)</c> convention, every clear orderable column, none.
/// <para>
/// Every key or index candidate is all-or-nothing: if one column is not readable in clear text for the caller, the whole
/// candidate is skipped (no prefix, no subset, no error that would reveal a hidden key column).
/// </para>
/// </summary>
public static class DefaultReadOrdering
{
    public static DefaultOrdering Resolve(TableMetadata metadata, TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);

        var clear = (TableColumn c) => decision.GetEffectiveColumnAccess(c.ColumnName, metadata) == ColumnAccessLevel.Clear;

        if (TryKey(metadata, clear, out var key, out var kind))
        {
            return new DefaultOrdering(key, kind);
        }

        var composite = metadata.Columns
            .Where(c => clear(c) && IsOrderableType(c.DataType, metadata.Dialect))
            .Select(c => c.ColumnName)
            .ToList();
        return composite.Count > 0
            ? new DefaultOrdering(composite, DefaultOrderingKind.ClearColumnComposite)
            : new DefaultOrdering(Array.Empty<string>(), DefaultOrderingKind.None);
    }

    /// <summary>
    /// The row identity candidate chain shared with <see cref="TableRowIdentity"/>; <paramref name="usable"/> decides whether a
    /// column may take part (clear access for ordering, presence for reporting).
    /// </summary>
    internal static bool TryKey(TableMetadata metadata, Func<TableColumn, bool> usable, out List<string> key, out DefaultOrderingKind kind)
    {
        kind = DefaultOrderingKind.PrimaryKey;
        if (TryAllUsable(metadata.PrimaryKeyColumns, metadata, usable, out key))
        {
            return true;
        }

        kind = DefaultOrderingKind.UniqueIndex;
        foreach (var index in metadata.Indexes
                     .Where(i => i is { IsUnique: true, Columns.Count: > 0 })
                     .OrderBy(i => i.Columns.Count))
        {
            if (TryAllUsable(index.Columns, metadata, usable, out key))
            {
                return true;
            }
        }

        // H2: declared rows win; the markers are a declaration channel only when no declared primary key exists, all-or-nothing.
        kind = DefaultOrderingKind.MetaMarkerKey;
        if (metadata.PrimaryKeyColumns.Count == 0 &&
            TableKeyMarkers.Parse(metadata.Columns) is { IsValid: true, Columns.Count: > 0 } markers &&
            TryAllUsable(markers.Columns, metadata, usable, out key))
        {
            return true;
        }

        // H1: the (client_id, ts) convention needs both columns; client_id alone is never a key.
        kind = DefaultOrderingKind.ConventionKey;
        if (TryAllUsable(["client_id", "ts"], metadata, usable, out key))
        {
            return true;
        }

        key = new List<string>();
        return false;
    }

    private static bool TryAllUsable(IReadOnlyList<string> candidates, TableMetadata metadata, Func<TableColumn, bool> usable, out List<string> key)
    {
        key = new List<string>(candidates.Count);
        if (candidates.Count == 0)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            var col = metadata.GetColumn(candidate);
            if (col == null || !usable(col))
            {
                key = new List<string>();
                return false;
            }

            if (!key.Contains(col.ColumnName, StringComparer.OrdinalIgnoreCase))
            {
                key.Add(col.ColumnName);
            }
        }

        return key.Count > 0;
    }

    private static readonly HashSet<string> NonOrderableBaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "jsonb", "xml",
        "binary", "varbinary", "bytea", "blob", "tinyblob", "mediumblob", "longblob", "image", "raw", "bfile",
        "geometry", "geography", "hierarchyid", "sdo_geometry", "spatial",
        "sql_variant", "variant",
        "array", "map", "struct", "list", "union", "row", "object"
    };

    private static readonly HashSet<string> LobTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "ntext", "image", "clob", "nclob"
    };

    private static readonly HashSet<string> GroupableBaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint", "int2", "int4", "int8", "serial", "smallserial", "bigserial",
        "bit", "bool", "boolean",
        "decimal", "numeric", "number", "money", "smallmoney", "float", "float4", "float8", "real", "double", "binary_float", "binary_double",
        "date", "time", "timetz", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "timestamp", "timestamptz", "interval",
        "uuid", "uniqueidentifier",
        "char", "nchar", "varchar", "nvarchar", "varchar2", "nvarchar2", "character", "bpchar", "citext", "string"
    };

    /// <summary>
    /// #398 allow-list of the column types a typed aggregation may group by. Unlike <see cref="IsOrderableType"/> an unknown, blank or
    /// unparsable type is refused (fail-closed) on every dialect except SQLite, which compares every value. <c>varchar(max)</c> and
    /// <c>nvarchar(max)</c> are groupable on SQL Server only; <c>text</c> only on PostgreSQL and Databricks. Binary, XML, JSON, spatial,
    /// structured and legacy LOB types (text/ntext/image, CLOB) stay refused.
    /// </summary>
    public static bool IsGroupableType(string? dataType, DatabaseDialect dialect)
    {
        if (dialect == DatabaseDialect.Sqlite)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(dataType))
        {
            return false;
        }

        var t = dataType.Trim().ToLowerInvariant();
        if (t.EndsWith("[]", StringComparison.Ordinal))
        {
            return false;
        }

        var end = t.IndexOfAny(['(', ' ', '<']);
        var baseType = end < 0 ? t : t[..end];
        if (t.Contains("(max)", StringComparison.Ordinal))
        {
            return dialect == DatabaseDialect.SqlServer && baseType is "varchar" or "nvarchar";
        }

        if (baseType == "text")
        {
            return dialect is DatabaseDialect.PostgreSql or DatabaseDialect.Databricks;
        }

        return GroupableBaseTypes.Contains(baseType);
    }

    /// <summary>
    /// False for document, binary, LOB, spatial and structured types that cannot (or should not) appear in an ORDER BY on the
    /// dialect. Unknown types count as orderable. <c>text</c> stays orderable on PostgreSQL, SQLite and Databricks.
    /// </summary>
    public static bool IsOrderableType(string? dataType, DatabaseDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return true;
        }

        var t = dataType.Trim().ToLowerInvariant();
        if (t.EndsWith("[]", StringComparison.Ordinal) || t.Contains("(max)", StringComparison.Ordinal))
        {
            return false;
        }

        if (t.StartsWith("long raw", StringComparison.Ordinal))
        {
            return false;
        }

        var end = t.IndexOfAny(['(', ' ', '<']);
        var baseType = end < 0 ? t : t[..end];
        if (NonOrderableBaseTypes.Contains(baseType))
        {
            return false;
        }

        if (dialect is DatabaseDialect.SqlServer or DatabaseDialect.Oracle)
        {
            if (LobTypes.Contains(baseType) || (dialect == DatabaseDialect.Oracle && baseType == "long"))
            {
                return false;
            }
        }

        return true;
    }
}
