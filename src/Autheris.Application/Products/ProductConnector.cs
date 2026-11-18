namespace Autheris.Application.Products;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources.Connectors;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Model;

/// <summary>
/// Stage 2 reader of one product object: the rows of the cached stage-1 result and nothing else. The connector offers no pushdown of any kind
/// (<see cref="ConnectorFeatures.None"/>), so the governed staging applies the caller's decision on the product object (row policy, masking,
/// denied columns) to every row. It never reads a base table; a missing, stale or foreign result fails closed.
/// </summary>
public sealed class ProductConnector : IAutherisConnector
{
    private readonly ProductRecordSource _records;

    public ProductConnector(IProductResultRouter router, ITableCacheStore store, TableIdentifier productObject)
    {
        _records = new ProductRecordSource(router ?? throw new ArgumentNullException(nameof(router)), store ?? throw new ArgumentNullException(nameof(store)), productObject);
    }

    public string ConnectorId => ProductSource.Name;

    public string ConnectorType => ProductSource.Name;

    public ConnectorCapabilities Capabilities { get; } = new(ConnectorFeatures.None, MaxBatchSize: 5000);

    public IConnectorMetadata Metadata { get; } = new NoMetadata();

    public IConnectorSplitManager SplitManager { get; } = new SingleSplit();

    public IConnectorRecordSource RecordSource => _records;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class NoMetadata : IConnectorMetadata
    {
        public Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<TableIdentifier>> ListTablesAsync(string? schema = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableIdentifier>>([]);

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<TableMetadata?>(null);
    }

    private sealed class SingleSplit : IConnectorSplitManager
    {
        public Task<IReadOnlyList<ConnectorSplit>> GetSplitsAsync(TableMetadata table, ConnectorSessionContext session, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConnectorSplit>>([ConnectorSplit.Default("product")]);
    }

    private sealed class ProductRecordSource(IProductResultRouter router, ITableCacheStore store, TableIdentifier productObject) : IConnectorRecordSource
    {
        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadSplitAsync(
            ConnectorSplit split, ConnectorSessionContext session, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var row in await ReadBatchAsync(split, session, ct).ConfigureAwait(false))
            {
                yield return row;
            }
        }

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadBatchAsync(ConnectorSplit split, ConnectorSessionContext session, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            if (session.Tenant is not { } tenant || !ProductSource.TryParse(productObject, tenant, out var schema, out var name))
            {
                throw new ProductLoadException(ProductErrorCodes.Unavailable);
            }

            // the router re-reads the product of the caller's tenant and loads or refuses per its mode; the key is that product's own result
            var route = await router.RouteAsync(tenant, productObject, ct).ConfigureAwait(false);
            var key = ProductSource.CacheKey(tenant, schema, name);
            if (route.Metadata is null || route.Metadata.Table != key)
            {
                throw new ProductLoadException(ProductErrorCodes.Unavailable);
            }

            var limit = session.Limit is > 0 ? session.Limit.Value : int.MaxValue;
            var view = route.Metadata.ActiveViewName.Replace("\"", "\"\"", StringComparison.Ordinal);
            var connection = await store.LeaseReaderConnectionAsync(key, ct).ConfigureAwait(false);
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT * FROM \"{view}\"";
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var rows = new List<IReadOnlyDictionary<string, object?>>();
                while (rows.Count < limit && await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }

                    rows.Add(row);
                }

                return rows;
            }
            finally
            {
                store.ReturnReaderConnection(key, connection);
            }
        }
    }
}
