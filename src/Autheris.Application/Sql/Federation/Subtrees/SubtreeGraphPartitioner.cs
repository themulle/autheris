namespace Autheris.Application.Sql.Federation.Subtrees;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine.Analysis;

public sealed record SubtreePartition(
    string GroupingKey,
    bool IsCoLocated,
    string? CoLocationGroup,
    IReadOnlyList<ResolvedSourceTable> Tables)
{
    public IReadOnlyList<string> DistinctDatabases => Tables
        .Select(t => t.Metadata.Table.PhysicalDatabaseName ?? t.Metadata.Identifier.Domain)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public bool IsMultiDatabase => DistinctDatabases.Count > 1;
}

/// <summary>
/// Issue #132 (PLAN-DATA-132, WP-132-3): Subtree graph partitioner for co-located and federated data sources.
/// Groups contiguous join components by `colocated:{CoLocationGroup}` when co-location is enabled,
/// or by `source:{SourceName}` otherwise.
/// </summary>
public static class SubtreeGraphPartitioner
{
    public static string ComputeGroupingKey(
        ResolvedSourceTable table,
        DataSourceConnectionOptions? connectionOptions,
        CrossSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);

        bool isCoLocationEnabled = options.CoLocation?.Enabled == true;
        if (isCoLocationEnabled &&
            connectionOptions != null &&
            !string.IsNullOrWhiteSpace(connectionOptions.CoLocationGroup) &&
            string.Equals(connectionOptions.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return $"colocated:{connectionOptions.CoLocationGroup}";
        }

        var source = table.Metadata.Table.SourceName ?? table.Metadata.Identifier.Domain;
        return $"source:{source}";
    }

    public static IReadOnlyList<SubtreePartition> Partition(
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<string, DataSourceConnectionOptions>? connectionOptionsBySource,
        CrossSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(options);

        var partitions = new List<SubtreePartition>();
        var grouped = tables.GroupBy(t =>
        {
            var source = t.Metadata.Table.SourceName ?? t.Metadata.Identifier.Domain;
            DataSourceConnectionOptions? conn = null;
            connectionOptionsBySource?.TryGetValue(source, out conn);
            return ComputeGroupingKey(t, conn, options);
        });

        foreach (var g in grouped)
        {
            bool isCoLocated = g.Key.StartsWith("colocated:", StringComparison.OrdinalIgnoreCase);
            string? groupName = isCoLocated ? g.Key["colocated:".Length..] : null;
            partitions.Add(new SubtreePartition(
                GroupingKey: g.Key,
                IsCoLocated: isCoLocated,
                CoLocationGroup: groupName,
                Tables: g.ToList()));
        }

        return partitions;
    }
}
