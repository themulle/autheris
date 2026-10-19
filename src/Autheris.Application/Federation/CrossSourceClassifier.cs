namespace Autheris.Application.Federation;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>The resolved physical identity of one requested table: the connector that reads it, its source label and its type.</summary>
public readonly record struct ResolvedSource(string ConnectorId, string SourceLabel, DataSourceType DataSourceType)
{
    /// <summary>Builds the identity from catalog metadata; the label is the source name, or the domain when none is set.</summary>
    public static ResolvedSource From(string connectorId, TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new ResolvedSource(connectorId, string.IsNullOrWhiteSpace(metadata.Table.SourceName) ? metadata.Identifier.Domain : metadata.Table.SourceName, metadata.Table.DataSourceType);
    }
}

/// <summary>
/// Decides whether a set of resolved tables is a cross-source request: more than one distinct connector id, more than one distinct
/// source label (both compared case-insensitively), or any table that is not a plain SQL table.
/// </summary>
public static class CrossSourceClassifier
{
    public static bool IsCrossSource(IEnumerable<ResolvedSource> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var connectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            if (table.DataSourceType != DataSourceType.Sql)
            {
                return true;
            }

            connectors.Add(table.ConnectorId);
            labels.Add(table.SourceLabel);
        }

        return connectors.Count > 1 || labels.Count > 1;
    }
}
