namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;

/// <summary>
/// Repairs legacy active SQL catalog rows whose persisted <c>source_type</c> is not a supported SQL dialect
/// (for example a connection name written by older releases) using the configured connection provider.
/// </summary>
public static class CatalogLegacySourceTypeHealer
{
    public static async Task<int> HealConfiguredSqlTablesAsync(
        ITableMetadataRepository repository,
        GatewayOptions options,
        ICatalogWriteScopeFactory? writeScopeFactory = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);

        var candidates = await repository.GetUnparseableSqlSourceTypesAsync(ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var toHeal = new List<TableMetadata>();
        var seen = new HashSet<TableIdentifier>();
        foreach (var (identifier, sourceName, _) in candidates)
        {
            var connectionName = string.IsNullOrWhiteSpace(sourceName) ? identifier.Domain : sourceName;
            var connectionOptions = SqlDataSourceResolver.ResolveConnectionOptions(options, connectionName);
            if (connectionOptions is null || !DataSourceProvider.TryResolveDialect(connectionOptions.Provider, out var dialect))
            {
                continue;
            }

            if (!seen.Add(identifier))
            {
                continue;
            }

            TableMetadata? meta;
            try
            {
                meta = await repository.GetTableMetadataAsync(identifier, ct).ConfigureAwait(false);
            }
            catch (TableMetadataUnavailableException ex)
            {
                logger?.LogWarning(ex, "Skipped legacy source_type healing for table {Table} because its metadata is unavailable.", identifier);
                continue;
            }

            if (meta is null ||
                !meta.Table.IsActive ||
                meta.Table.DataSourceType != DataSourceType.Sql ||
                DatabaseDialectExtensions.TryParseDialect(meta.Table.SourceType, out _))
            {
                continue;
            }

            toHeal.Add(meta with
            {
                Table = meta.Table with { SourceType = dialect.ToString() }
            });
        }

        if (toHeal.Count == 0)
        {
            return 0;
        }

        await using var writeScope = writeScopeFactory?.Begin("legacy-source-type-heal");
        await repository.UpsertTableMetadataBatchAsync(toHeal, ct).ConfigureAwait(false);
        logger?.LogInformation("Healed legacy source_type on {Count} configured SQL catalog table(s).", toHeal.Count);
        return toHeal.Count;
    }
}
