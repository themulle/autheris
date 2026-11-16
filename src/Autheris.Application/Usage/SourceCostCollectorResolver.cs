namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Options;

/// <summary>Matches configured data sources to registered <see cref="ISourceCostCollector"/> instances.</summary>
public static class SourceCostCollectorResolver
{
    public static ISourceCostCollector? FindForDataSource(
        string? dataSource,
        GatewayOptions gatewayOptions,
        IReadOnlyList<ISourceCostCollector> collectors)
    {
        ArgumentNullException.ThrowIfNull(gatewayOptions);
        ArgumentNullException.ThrowIfNull(collectors);

        if (string.IsNullOrWhiteSpace(dataSource) ||
            !gatewayOptions.DataSources.Connections.TryGetValue(dataSource, out var conn))
        {
            return null;
        }

        bool hasConnDialect = DataSourceProvider.TryResolveDialect(conn.Provider, out var connDialect);
        for (int i = 0; i < collectors.Count; i++)
        {
            var collector = collectors[i];
            if ((hasConnDialect &&
                 DataSourceProvider.TryResolveDialect(collector.SourceType, out var colDialect) &&
                 connDialect == colDialect) ||
                string.Equals(collector.SourceType, conn.Provider, StringComparison.OrdinalIgnoreCase))
            {
                return collector;
            }
        }

        return null;
    }
}
