namespace Autheris.Infrastructure.Connectors;

using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class MilvusVectorConnector : IVectorAutherisConnector, IVectorRecordSource
{
    private readonly Func<VectorSearchRequest, ConnectorSessionContext, CancellationToken, Task<IReadOnlyList<VectorDocumentChunk>>>? _customExecutor;
    private readonly ILogger<MilvusVectorConnector>? _logger;

    public string ConnectorId { get; }
    public string ConnectorType => "milvus";
    public ConnectorCapabilities Capabilities { get; } = new(
        ConnectorFeatures.FilterPushdown | ConnectorFeatures.ProjectionPushdown | ConnectorFeatures.LimitPushdown,
        MaxBatchSize: 500);

    public IConnectorMetadata Metadata { get; }
    public IConnectorSplitManager SplitManager { get; }
    public IConnectorRecordSource RecordSource => new EmptyRecordSource();
    public IVectorRecordSource VectorRecordSource => this;

    public MilvusVectorConnector(
        string connectorId = "milvus-default",
        Func<VectorSearchRequest, ConnectorSessionContext, CancellationToken, Task<IReadOnlyList<VectorDocumentChunk>>>? customExecutor = null,
        ILogger<MilvusVectorConnector>? logger = null)
    {
        ConnectorId = connectorId;
        _customExecutor = customExecutor;
        _logger = logger;
        Metadata = new DefaultVectorMetadata();
        SplitManager = new DefaultVectorSplitManager();
    }

    public async Task<IReadOnlyList<VectorDocumentChunk>> SearchVectorsAsync(
        VectorSearchRequest request,
        ConnectorSessionContext session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);

        if (session.Tenant == null || string.IsNullOrWhiteSpace(session.Tenant.Value.Value))
        {
            throw new SecurityException("INV-VEC-01: Milvus search rejected. TenantId missing in session context.");
        }

        var tenantId = session.Tenant.Value;

        // Build native Milvus boolean expression enforcing partition key
        var expr = VectorPushdownSecurityHelper.BuildMilvusFilter(request, tenantId);
        _logger?.LogDebug("Built Milvus partition expr: {Expr}", expr);

        if (_customExecutor != null)
        {
            var rawResults = await _customExecutor(request, session, ct).ConfigureAwait(false);
            return VectorPushdownSecurityHelper.FilterChunksByRls(rawResults, tenantId);
        }

        return Array.Empty<VectorDocumentChunk>();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class EmptyRecordSource : IConnectorRecordSource
    {
        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadSplitAsync(
            ConnectorSplit split, ConnectorSessionContext session, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield break;
        }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadBatchAsync(
            ConnectorSplit split, ConnectorSessionContext session, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Array.Empty<IReadOnlyDictionary<string, object?>>());
    }

    private sealed class DefaultVectorMetadata : IConnectorMetadata
    {
        public Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<TableIdentifier>> ListTablesAsync(string? schema = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableIdentifier>>(Array.Empty<TableIdentifier>());

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier identifier, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(null);
    }

    private sealed class DefaultVectorSplitManager : IConnectorSplitManager
    {
        public Task<IReadOnlyList<ConnectorSplit>> GetSplitsAsync(
            TableMetadata metadata, ConnectorSessionContext session, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConnectorSplit>>(Array.Empty<ConnectorSplit>());
    }
}
