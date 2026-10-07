using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Connectors;

public sealed class SqlConnector : IAutherisConnector
{
    public string ConnectorId { get; }
    public string ConnectorType => "sql";
    public ConnectorCapabilities Capabilities { get; }
    public IConnectorMetadata Metadata { get; }
    public IConnectorSplitManager SplitManager { get; }
    public IConnectorRecordSource RecordSource { get; }

    public SqlConnector(
        string connectorId,
        ISqlConnectionFactory connectionFactory,
        ITableMetadataRepository metadataRepository,
        IOptions<GatewayOptions>? options = null,
        ILogger<SqlConnector>? logger = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
        ConnectorCapabilities? customCapabilities = null,
        IColumnMaskingProvider? maskingProvider = null)
    {
        ConnectorId = string.IsNullOrWhiteSpace(connectorId) ? "sql" : connectorId;
        Capabilities = customCapabilities ?? ConnectorCapabilities.DefaultSql;
        Metadata = new SqlConnectorMetadata(metadataRepository);
        SplitManager = new SqlConnectorSplitManager();
        RecordSource = new SqlConnectorRecordSource(
            connectionFactory,
            options,
            logger,
            environment,
            ConnectorId,
            maskingProvider);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class SqlConnectorMetadata : IConnectorMetadata
    {
        private readonly ITableMetadataRepository _repository;

        public SqlConnectorMetadata(ITableMetadataRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public async Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct = default)
        {
            var tables = await _repository.GetAllTablesAsync(ct);
            return tables.Select(t => t.Table.SchemaName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<IReadOnlyList<TableIdentifier>> ListTablesAsync(string? schema = null, CancellationToken ct = default)
        {
            var tables = await _repository.GetAllTablesAsync(ct);
            if (!string.IsNullOrWhiteSpace(schema))
            {
                tables = tables.Where(t => string.Equals(t.Table.SchemaName, schema, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            return tables.Select(t => t.Identifier).ToList();
        }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
        {
            return _repository.GetTableMetadataAsync(table, ct);
        }
    }

    private sealed class SqlConnectorSplitManager : IConnectorSplitManager
    {
        public Task<IReadOnlyList<ConnectorSplit>> GetSplitsAsync(
            TableMetadata table,
            ConnectorSessionContext session,
            CancellationToken ct = default)
        {
            // For standard SQL queries, single partition split with table context
            var props = new Dictionary<string, object?>
            {
                ["Schema"] = table.Table.SchemaName,
                ["Table"] = table.Table.TableName,
                ["Domain"] = table.Identifier.Domain
            };

            IReadOnlyList<ConnectorSplit> splits = new[]
            {
                new ConnectorSplit($"split-{table.Identifier}", props)
            };

            return Task.FromResult(splits);
        }
    }

    private sealed class SqlConnectorRecordSource : IConnectorRecordSource
    {
        private readonly SqlDataSourceExecutor _executor;
        private readonly string _connectorId;
        private readonly IOptions<GatewayOptions>? _options;

        public SqlConnectorRecordSource(
            ISqlConnectionFactory connectionFactory,
            IOptions<GatewayOptions>? options,
            ILogger<SqlConnector>? logger,
            Microsoft.Extensions.Hosting.IHostEnvironment? environment,
            string connectorId,
            IColumnMaskingProvider? maskingProvider)
        {
            // SEC H-13: Without the masking provider HMAC columns would only be redacted instead of pseudonymized.
            _executor = new SqlDataSourceExecutor(connectionFactory, options, null, environment, maskingProvider);
            _connectorId = connectorId;
            _options = options;
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
            ArgumentNullException.ThrowIfNull(session);

            if (!session.Items.TryGetValue("TableMetadata", out var metaObj) || metaObj is not TableMetadata meta)
            {
                throw new InvalidOperationException("TableMetadata must be present in ConnectorSessionContext.Items.");
            }

            ConnectorSecurityPolicyEvaluator.EnforceSecurityPolicy(session, meta);

            // SQL2-2: Dynamic ceiling based on options (Olap MaxStagedRowsPerTable / Export MaxExportRows)
            // instead of hardcoding 5000 which truncated OLAP queries silently.
            var maxAllowed = Math.Max(5000, Math.Max(
                _options?.Value?.DuckDbOlap?.MaxStagedRowsPerTable ?? 250_000,
                _options?.Value?.Arrow?.MaxExportRows ?? 1_000_000));
            if (session.Limit.HasValue && session.Limit.Value > maxAllowed)
            {
                maxAllowed = session.Limit.Value;
            }

            var defaultLimit = session.Limit ?? 1000;
            var clampedLimit = Math.Clamp(defaultLimit, 1, maxAllowed);

            var dsContext = new DataSourceExecutionContext(
                // The connection is configured per data source (Gateway:DataSources:Connections:<source>), so the
                // table's own source name must be used; the connector id ("default-sql") never matches a connection.
                SourceName: string.IsNullOrWhiteSpace(meta.Table.SourceName) ? _connectorId : meta.Table.SourceName,
                Metadata: meta,
                Principal: session.Principal,
                AccessDecision: session.AccessDecision,
                Arguments: session.Arguments,
                RequestedFields: session.ProjectedColumns,
                RequestHeaders: session.RequestHeaders,
                Limit: clampedLimit,
                Offset: Math.Max(0, session.Offset ?? 0),
                Tenant: session.Tenant,
                Items: session.Items);

            return await _executor.ExecuteAsync(dsContext, ct);
        }
    }
}
