namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
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

        kind = DefaultOrderingKind.MetaMarkerKey;
        if (TryAllUsable(ExtractMetaPrimaryKeyColumns(metadata.Columns), metadata, usable, out key))
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

    /// <summary>
    /// Column <c>Meta</c> markers as a declaration channel (<c>primary_key_ordinal</c>, <c>is_primary_key=true</c>,
    /// <c>role=primary_key</c>), validated like a declared key and fail closed: a non-numeric or non-positive ordinal, a
    /// duplicate or a gap makes the whole marker key invalid (empty result). Ordinals win over flags.
    /// </summary>
    public static IReadOnlyList<string> ExtractMetaPrimaryKeyColumns(IReadOnlyList<TableColumn> columns)
    {
        if (columns == null || columns.Count == 0)
        {
            return Array.Empty<string>();
        }

        var ordinalCols = new List<(int Ordinal, string Name)>();
        foreach (var col in columns)
        {
            if (col.Meta != null && col.Meta.TryGetValue("primary_key_ordinal", out var ordStr))
            {
                if (!int.TryParse(ordStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ord) || ord < 1)
                {
                    return Array.Empty<string>();
                }

                ordinalCols.Add((ord, col.ColumnName));
            }
        }

        if (ordinalCols.Count > 0)
        {
            var sorted = ordinalCols.OrderBy(x => x.Ordinal).ToList();
            for (var i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].Ordinal != i + 1)
                {
                    return Array.Empty<string>();
                }
            }

            var names = sorted.Select(x => x.Name).ToList();
            return names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count ? names : Array.Empty<string>();
        }

        var flagCols = new List<string>();
        foreach (var col in columns)
        {
            if (col.Meta == null)
            {
                continue;
            }

            if ((col.Meta.TryGetValue("is_primary_key", out var isPk) && string.Equals(isPk, "true", StringComparison.OrdinalIgnoreCase)) ||
                (col.Meta.TryGetValue("role", out var role) && string.Equals(role, "primary_key", StringComparison.OrdinalIgnoreCase)))
            {
                flagCols.Add(col.ColumnName);
            }
        }

        return flagCols.Count > 0 && flagCols.Distinct(StringComparer.OrdinalIgnoreCase).Count() == flagCols.Count
            ? flagCols
            : Array.Empty<string>();
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
