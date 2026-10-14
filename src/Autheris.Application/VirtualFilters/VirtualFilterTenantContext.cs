namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;

/// <summary>
/// SR-L-11: the caller's tenant and the tenant column of every catalogued source table, so that the predicate of a virtual filter
/// restricts each tenanted source table to the caller's tenant (like the federation key query). <see cref="TenantColumnOf"/>
/// returns the tenant column of a table, null when the table is not tenanted (or exempt), and throws
/// <see cref="InvalidOperationException"/> when the table is unknown or required to be tenanted and is not (fail closed).
/// </summary>
/// <param name="Tenant">The caller's tenant, from the resolved principal; never from filter text.</param>
/// <param name="TenantColumnOf">Tenant column per source table.</param>
public sealed record VirtualFilterTenantContext(string Tenant, Func<TableIdentifier, string?> TenantColumnOf)
{
    /// <summary>Alias to tenant column of every table of a structured definition that is tenanted.</summary>
    public IReadOnlyDictionary<string, string> ColumnsByAlias(StructuredFilterDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tables = new List<(TableIdentifier Table, string Alias)> { (definition.From, definition.FromAlias) };
        foreach (var join in definition.Joins)
        {
            tables.Add((join.Table, join.Alias));
        }

        foreach (var (table, alias) in tables)
        {
            var column = TenantColumnOf(table);
            if (column != null)
            {
                result[alias] = column;
            }
        }

        return result;
    }
}

/// <summary>Shared tenant column resolution of virtual filter source tables (SR-L-11).</summary>
public static class VirtualFilterTenantColumns
{
    /// <summary>
    /// The tenant column of <paramref name="metadata"/> through <see cref="TableMetadata.RequireTenantColumnOrThrow"/>: null when
    /// the table is not tenanted (and not required to be). A required but missing tenant column is an
    /// <see cref="InvalidOperationException"/> (fail closed).
    /// </summary>
    public static string? Resolve(TableMetadata metadata, bool requireTenantColumn, IEnumerable<string>? exemptTables)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        try
        {
            return TableMetadata.RequireTenantColumnOrThrow(metadata, requireTenantColumn, exemptTables);
        }
        catch (System.Security.SecurityException ex)
        {
            throw new InvalidOperationException(
                $"The virtual filter source table '{metadata.Identifier}' has no tenant column and is not exempt (fail-closed).", ex);
        }
    }
}
