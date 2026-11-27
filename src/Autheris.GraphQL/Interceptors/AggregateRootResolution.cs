namespace Autheris.GraphQL.Interceptors;

using System;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;

/// <summary>Maps a root field name (table, <c>_aggregate</c>, <c>_groups</c> or <c>Connection</c>) to its table metadata.</summary>
internal static class AggregateRootResolution
{
    private const string ConnectionSuffix = "Connection";

    /// <summary>The table of a root field name, or <c>null</c> when none resolves; <paramref name="baseName"/> is the derived table name.</summary>
    public static TableMetadata? Resolve(string rootName, Func<string, TableMetadata?> metadataLookup, out string? baseName)
    {
        ArgumentNullException.ThrowIfNull(rootName);
        ArgumentNullException.ThrowIfNull(metadataLookup);

        baseName = null;
        if (metadataLookup(rootName) is { } direct)
        {
            return direct;
        }

        if (AggregateTypeNames.TryGetAggregateRootBase(rootName, out var aggregateBase))
        {
            baseName = aggregateBase;
        }
        else if (rootName.EndsWith(ConnectionSuffix, StringComparison.Ordinal))
        {
            baseName = rootName[..^ConnectionSuffix.Length];
        }

        return baseName is null ? null : metadataLookup(baseName);
    }
}
