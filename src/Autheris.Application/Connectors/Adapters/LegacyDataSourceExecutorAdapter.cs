using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Model;

namespace Autheris.Application.Connectors.Adapters;

/// <summary>
/// Bridges an existing IDataSourceExecutor to the Trino-like IAutherisConnector SPI.
/// </summary>
public sealed class LegacyDataSourceExecutorAdapter : IAutherisConnector
{
    private readonly IDataSourceExecutor _executor;
    private readonly ITableMetadataRepository? _metadataRepository;

    public string ConnectorId { get; }
    public string ConnectorType { get; }
    public ConnectorCapabilities Capabilities { get; }
    public IConnectorMetadata Metadata { get; }
    public IConnectorSplitManager SplitManager { get; }
    public IConnectorRecordSource RecordSource { get; }

    public LegacyDataSourceExecutorAdapter(
        IDataSourceExecutor executor,
        string? connectorId = null,
        ITableMetadataRepository? metadataRepository = null,
        ConnectorCapabilities? capabilities = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _metadataRepository = metadataRepository;
        ConnectorType = executor.SupportedType.ToString().ToLowerInvariant();
        ConnectorId = connectorId ?? $"{ConnectorType}-legacy-connector";

        Capabilities = capabilities ?? (executor.SupportedType == DataSourceType.Sql
            ? ConnectorCapabilities.DefaultSql
            : ConnectorCapabilities.DefaultHttp);

        Metadata = new LegacyConnectorMetadata(_metadataRepository);
        SplitManager = new SingleSplitManager();
        RecordSource = new LegacyConnectorRecordSource(_executor, ConnectorId);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class LegacyConnectorMetadata : IConnectorMetadata
    {
        private readonly ITableMetadataRepository? _repo;

        public LegacyConnectorMetadata(ITableMetadataRepository? repo)
        {
            _repo = repo;
        }

        public async Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct = default)
        {
            if (_repo == null) return Array.Empty<string>();
            var all = await _repo.GetAllTablesAsync(ct);
            return all.Select(t => t.Table.SchemaName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<IReadOnlyList<TableIdentifier>> ListTablesAsync(string? schema = null, CancellationToken ct = default)
        {
            if (_repo == null) return Array.Empty<TableIdentifier>();
            var all = await _repo.GetAllTablesAsync(ct);
            if (!string.IsNullOrWhiteSpace(schema))
            {
                all = all.Where(t => string.Equals(t.Table.SchemaName, schema, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            return all.Select(t => t.Identifier).ToList();
        }

        public async Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
        {
            if (_repo == null) return null;
            return await _repo.GetTableMetadataAsync(table, ct);
        }
    }

    private sealed class SingleSplitManager : IConnectorSplitManager
    {
        public Task<IReadOnlyList<ConnectorSplit>> GetSplitsAsync(
            TableMetadata table,
            ConnectorSessionContext session,
            CancellationToken ct = default)
        {
            IReadOnlyList<ConnectorSplit> splits = new[] { ConnectorSplit.Default($"split-{table.Identifier}") };
            return Task.FromResult(splits);
        }
    }

    private sealed class LegacyConnectorRecordSource : IConnectorRecordSource
    {
        private readonly IDataSourceExecutor _executor;
        private readonly string _connectorId;

        public LegacyConnectorRecordSource(IDataSourceExecutor executor, string connectorId)
        {
            _executor = executor;
            _connectorId = connectorId;
        }

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadSplitAsync(
            ConnectorSplit split,
            ConnectorSessionContext session,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var batch = await ReadBatchAsync(split, session, ct);
            foreach (var row in batch)
            {
                yield return row;
            }
        }

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadBatchAsync(
            ConnectorSplit split,
            ConnectorSessionContext session,
            CancellationToken ct = default)
        {
            // TableMetadata is retrieved or constructed
            if (!session.Items.TryGetValue("TableMetadata", out var metaObj) || metaObj is not TableMetadata meta)
            {
                throw new InvalidOperationException("TableMetadata must be present in ConnectorSessionContext.Items for LegacyDataSourceExecutorAdapter.");
            }

            var dsContext = new DataSourceExecutionContext(
                SourceName: _connectorId,
                Metadata: meta,
                Principal: session.Principal,
                AccessDecision: session.AccessDecision,
                Arguments: session.Arguments,
                RequestedFields: session.ProjectedColumns,
                RequestHeaders: session.RequestHeaders,
                Limit: session.Limit ?? 1000,
                Offset: session.Offset ?? 0,
                Tenant: session.Tenant,
                Items: session.Items);

            return await _executor.ExecuteAsync(dsContext, ct);
        }
    }
}
