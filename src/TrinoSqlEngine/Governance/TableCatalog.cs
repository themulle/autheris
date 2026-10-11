namespace TrinoSqlEngine.Governance;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>Canonical identity of a physical table: the exact catalog spelling of schema and table (INV-11).</summary>
public readonly record struct TableIdentity(string Schema, string Table, string? Catalog = null)
{
    public override string ToString() => Catalog is null ? $"{Schema}.{Table}" : $"{Catalog}.{Schema}.{Table}";

    /// <summary>The delimited canonical name: <c>[catalog.]schema.table</c>, every part quoted, exact case.</summary>
    public SqlQualifiedName ToQualifiedName() => new(
        (Catalog is null
            ? new[] { new SqlIdentifier(Schema, true), new SqlIdentifier(Table, true) }
            : new[] { new SqlIdentifier(Catalog, true), new SqlIdentifier(Schema, true), new SqlIdentifier(Table, true) }));
}

/// <param name="Name">Canonical column name (exact case).</param>
/// <param name="DataType">Catalog data type (provider native), for example <c>nvarchar(64)</c> or <c>int</c>.</param>
/// <param name="Collation">Column collation when the catalog knows it (for example <c>UTF8_BINARY</c> on Databricks); null when unknown, which is treated as "not binary" (fail safe).</param>
public sealed record CatalogColumn(string Name, string DataType, string? Collation = null);

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
    private readonly FrozenDictionary<string, ImmutableArray<TableCatalogEntry>> _bySchemaTable;
    private readonly string _defaultSchema;

    public InMemoryTableCatalog(IEnumerable<TableCatalogEntry> entries, string defaultSchema)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _defaultSchema = defaultSchema ?? throw new ArgumentNullException(nameof(defaultSchema));
        var map = new Dictionary<string, List<TableCatalogEntry>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            string key = Key(entry.Identity.Schema, entry.Identity.Table);
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<TableCatalogEntry>(1);
            list.Add(entry);
        }

        _bySchemaTable = map.ToFrozenDictionary(static p => p.Key, static p => p.Value.ToImmutableArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// A name with one or two parts resolves only when exactly one entry has that schema and table, so two catalogs (or two
    /// spellings that differ by case) with the same <c>schema.table</c> are ambiguous. A three-part name selects the Unity
    /// Catalog part of an entry that has one (CR-ADG-17); for entries without a catalog part the first part is the Trino catalog
    /// name and is ignored.
    /// </summary>
    public TableCatalogEntry? Resolve(SqlQualifiedName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string schema, table;
        string? catalog = null;
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
                catalog = name.Parts[0].Value;
                schema = name.Parts[1].Value;
                table = name.Parts[2].Value;
                break;
            default:
                return null;
        }

        if (!_bySchemaTable.TryGetValue(Key(schema, table), out var candidates))
        {
            return null;
        }

        TableCatalogEntry? match = null;
        foreach (var candidate in candidates)
        {
            if (catalog is not null && candidate.Identity.Catalog is not null &&
                !string.Equals(candidate.Identity.Catalog, catalog, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                return null;   // ambiguous: never guess a table
            }

            match = candidate;
        }

        return match;
    }

    private static string Key(string schema, string table) =>
        schema.ToLowerInvariant() + "\u0001" + table.ToLowerInvariant();
}
