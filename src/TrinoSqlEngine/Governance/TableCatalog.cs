namespace TrinoSqlEngine.Governance;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>Canonical identity of a physical table: the exact catalog spelling of schema and table (INV-11).</summary>
public readonly record struct TableIdentity(string Schema, string Table)
{
    public override string ToString() => $"{Schema}.{Table}";
}

/// <param name="Name">Canonical column name (exact case).</param>
/// <param name="DataType">Catalog data type, for example <c>nvarchar(64)</c> or <c>int</c>.</param>
public sealed record CatalogColumn(string Name, string DataType);

/// <param name="Identity">Canonical schema-qualified table identity.</param>
/// <param name="Columns">The columns the gateway exposes. A secured table projects exactly these columns.</param>
/// <param name="TenantColumn">The tenant isolation column, or null for a table without tenant scoping.</param>
/// <param name="Version">Catalog version of this entry; part of every cache key.</param>
public sealed record TableCatalogEntry(
    TableIdentity Identity,
    ImmutableArray<CatalogColumn> Columns,
    string? TenantColumn,
    long Version = 0);

/// <summary>Resolves user-written table names to catalog entries. An unresolved name is never emitted (INV-11).</summary>
public interface ITableCatalog
{
    /// <summary>The entry for <paramref name="name"/>, or null when the name is unknown or ambiguous.</summary>
    TableCatalogEntry? Resolve(SqlQualifiedName name);
}

/// <summary>
/// Simple in-memory catalog. Names resolve case-insensitively against the canonical spelling; a name that matches more than
/// one entry (they differ only by case) is ambiguous and does not resolve.
/// </summary>
public sealed class InMemoryTableCatalog : ITableCatalog
{
    private readonly FrozenDictionary<string, TableCatalogEntry?> _bySchemaTable;
    private readonly string _defaultSchema;

    public InMemoryTableCatalog(IEnumerable<TableCatalogEntry> entries, string defaultSchema)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _defaultSchema = defaultSchema ?? throw new ArgumentNullException(nameof(defaultSchema));
        var map = new Dictionary<string, TableCatalogEntry?>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            string key = Key(entry.Identity.Schema, entry.Identity.Table);
            // A second entry with the same case-folded key makes the name ambiguous: neither resolves.
            map[key] = map.ContainsKey(key) ? null : entry;
        }

        _bySchemaTable = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public TableCatalogEntry? Resolve(SqlQualifiedName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string schema, table;
        switch (name.Parts.Count)
        {
            case 1:
                schema = _defaultSchema;
                table = name.Parts[0].Value;
                break;
            case 2:
                schema = name.Parts[0].Value;
                table = name.Parts[1].Value;
                break;
            case 3:
                // catalog.schema.table: the catalog part is a Trino catalog name, not a database of the target.
                schema = name.Parts[1].Value;
                table = name.Parts[2].Value;
                break;
            default:
                return null;
        }

        return _bySchemaTable.TryGetValue(Key(schema, table), out var entry) ? entry : null;
    }

    private static string Key(string schema, string table) =>
        schema.ToLowerInvariant() + "\u0001" + table.ToLowerInvariant();
}
