namespace Autheris.Extensions.DataCatalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// OpenMetadata as data catalog provider. SEC E-09 / EX-06: applies <c>OpenMetadata.ServiceFilter</c> and the
/// database-aware identity (<c>OpenMetadata.ServiceDatabaseToDomainMap</c>, collision rejection) exactly like the
/// OpenMetadata permission sync (<see cref="OpenMetadataTableIdentity"/>).
/// </summary>
public sealed class OpenMetadataCatalogAdapter : IDataCatalogClient
{
    private readonly IOpenMetadataClient _client;
    private readonly ILogger<OpenMetadataCatalogAdapter> _logger;
    private readonly IOptions<GatewayOptions>? _options;

    public DataCatalogProviderType ProviderType => DataCatalogProviderType.OpenMetadata;

    public OpenMetadataCatalogAdapter(
        IOpenMetadataClient client,
        ILogger<OpenMetadataCatalogAdapter> logger,
        IOptions<GatewayOptions>? options = null)
    {
        _client = client;
        _logger = logger;
        _options = options;
    }

    private OpenMetadataOptions OmOptions => _options?.Value.OpenMetadata ?? new OpenMetadataOptions();

    public async Task<IReadOnlyList<CatalogTableAsset>> GetTablesAsync(string? filter = null, CancellationToken ct = default)
    {
        var omOptions = OmOptions;
        var serviceQuery = string.IsNullOrWhiteSpace(omOptions.ServiceFilter) ? filter : omOptions.ServiceFilter;
        var omTables = await _client.GetTablesAsync(serviceQuery, ct).ConfigureAwait(false);

        var candidates = new List<(OpenMetadataTable Item, OpenMetadataTableIdentity.ResolvedTable Resolved)>(omTables.Count);
        foreach (var omTable in omTables)
        {
            if (OpenMetadataTableIdentity.TryResolve(omTable, omOptions, out var resolved, out var reason))
            {
                candidates.Add((omTable, resolved));
            }
            else
            {
                _logger.LogWarning("Skipping OpenMetadata catalog table {Fqn}: {Reason}.", omTable.FullyQualifiedName, reason);
            }
        }

        if (omOptions.ServiceDatabaseToDomainMap.Count == 0)
        {
            candidates = OpenMetadataTableIdentity.RejectDatabaseCollisions(candidates, id =>
                _logger.LogWarning("Rejecting OpenMetadata catalog tables mapping to {Table}: same service.schema.table in different databases (configure OpenMetadata.ServiceDatabaseToDomainMap).", id));
        }

        var list = new List<CatalogTableAsset>(candidates.Count);
        foreach (var (omTable, resolved) in candidates)
        {
            list.Add(MapToCatalogAsset(omTable, resolved.Identifier));
        }

        return list;
    }

    public async Task<CatalogTableAsset?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var fqn = $"{table.Domain}.{table.Schema}.{table.TableName}";
        try
        {
            var omTable = await _client.GetTableByFqnAsync(fqn, ct).ConfigureAwait(false);
            if (omTable == null)
            {
                return null;
            }

            // SEC E-09: the returned table must pass the ServiceFilter / domain mapping and resolve to the requested identity.
            if (!OpenMetadataTableIdentity.TryResolve(omTable, OmOptions, out var resolved, out var reason))
            {
                _logger.LogWarning("Ignoring OpenMetadata catalog table {Fqn}: {Reason}.", omTable.FullyQualifiedName, reason);
                return null;
            }

            return resolved.Identifier.Equals(table) ? MapToCatalogAsset(omTable, resolved.Identifier) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get table {Table} from OpenMetadata", table);
            return null;
        }
    }

    private static CatalogTableAsset MapToCatalogAsset(OpenMetadataTable omTable, TableIdentifier tableId)
    {
        var columns = omTable.Columns.Select(c => new CatalogColumnAsset
        {
            ColumnName = c.Name,
            DataType = c.DataType,
            Description = c.Description,
            Tags = c.Tags.Select(t => t.TagFQN).ToList(),
            Classifications = c.Tags.Select(t => t.TagFQN).ToList(),
            IsNullable = true
        }).ToList();

        var owner = omTable.Owners.FirstOrDefault()?.Name;

        return new CatalogTableAsset
        {
            Identifier = tableId,
            DisplayName = omTable.DisplayName ?? omTable.Name,
            Description = omTable.Description,
            SourceType = omTable.ServiceType ?? "PostgreSQL",
            ExternalAssetId = omTable.Id.ToString(),
            OwnerRef = owner,
            Tags = omTable.Tags.Select(t => t.TagFQN).ToList(),
            Classifications = omTable.Tags.Select(t => t.TagFQN).ToList(),
            Columns = columns
        };
    }
}
