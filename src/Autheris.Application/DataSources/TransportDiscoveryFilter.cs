namespace Autheris.Application.DataSources;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;

/// <summary>
/// Issue #447 (decision 10): discovery hides, it never errors. A table whose source does not allow the ingress transport is
/// dropped from catalog listings, schemas and metadata documents, so a caller cannot learn that a source exists or which transports it
/// allows. Hiding is not the enforcement point (the gate at the data choke points is); a dropped row therefore never
/// widens access, and every doubt (resolver failure, invalid transport) hides.
/// </summary>
public static class TransportDiscoveryFilter
{
    /// <summary>
    /// True when the table may be listed over <paramref name="transport"/>. No resolver, or no ingress at all (an admin or internal
    /// context: the endpoint is exempt and reaches no data), leaves discovery unfiltered. Catalog-named sources without a profile are
    /// judged by <c>Defaults</c> (owner decision 1).
    /// </summary>
    public static bool IsVisible(IDataSourceOptionsResolver? resolver, DataSourceTransportFlags transport, TableMetadata table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return IsSourceVisible(resolver, transport, DataSourceAllowlistGuard.LogicalDataSource(table));
    }

    /// <summary>Source-level form of <see cref="IsVisible"/> for a logical catalog source name.</summary>
    public static bool IsSourceVisible(IDataSourceOptionsResolver? resolver, DataSourceTransportFlags transport, string logicalSource)
    {
        if (resolver is null || transport == DataSourceTransportFlags.None)
        {
            return true;
        }

        try
        {
            var canonical = resolver.CanonicalName(logicalSource);
            return DataSourceTransportGate.Decide(resolver, canonical, transport, catalogKnown: true).IsAllowed;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The tables visible over <paramref name="transport"/>, in their original order.</summary>
    public static IReadOnlyList<TableMetadata> Apply(IDataSourceOptionsResolver? resolver, DataSourceTransportFlags transport, IReadOnlyList<TableMetadata> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return resolver is null || transport == DataSourceTransportFlags.None
            ? tables
            : tables.Where(t => IsVisible(resolver, transport, t)).ToList();
    }
}
