using System.Runtime.CompilerServices;
using Autheris.Application.DataSources.Connectors;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Lightweight in-memory <see cref="IAutherisConnector"/> (also implementing metadata, split manager and record source)
/// for connector and federated staging tests.
/// </summary>
public sealed class StubAutherisConnector : IAutherisConnector, IConnectorMetadata, IConnectorSplitManager, IConnectorRecordSource
{
    private readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> _rows;
    private readonly IReadOnlyList<TableMetadata> _tables;
    private readonly Func<ConnectorSplit, ConnectorSessionContext, CancellationToken, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>>? _readBatch;
    private readonly Func<TableMetadata, ConnectorSessionContext, CancellationToken, Task<IReadOnlyList<ConnectorSplit>>>? _getSplits;

    public StubAutherisConnector(
        string connectorId,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows = null,
        string connectorType = "Sql",
        ConnectorCapabilities? capabilities = null,
        IReadOnlyList<TableMetadata>? tables = null,
        Func<ConnectorSplit, ConnectorSessionContext, CancellationToken, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>>? readBatch = null,
        Func<TableMetadata, ConnectorSessionContext, CancellationToken, Task<IReadOnlyList<ConnectorSplit>>>? getSplits = null)
    {
        ConnectorId = connectorId;
        ConnectorType = connectorType;
        Capabilities = capabilities ?? ConnectorCapabilities.DefaultSql;
        _rows = rows ?? [];
        _tables = tables ?? [];
        _readBatch = readBatch;
        _getSplits = getSplits;
    }

    public string ConnectorId { get; }
    public string ConnectorType { get; }
    public ConnectorCapabilities Capabilities { get; }
    public IConnectorMetadata Metadata => this;
    public IConnectorSplitManager SplitManager => this;
    public IConnectorRecordSource RecordSource => this;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(_tables.Select(t => t.Identifier.Schema).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

    public Task<IReadOnlyList<TableIdentifier>> ListTablesAsync(string? schema = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TableIdentifier>>(
            _tables.Where(t => schema is null || string.Equals(t.Identifier.Schema, schema, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Identifier)
                .ToList());

    public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
        => Task.FromResult(_tables.FirstOrDefault(t => t.Identifier == table));

    public Task<IReadOnlyList<ConnectorSplit>> GetSplitsAsync(TableMetadata table, ConnectorSessionContext session, CancellationToken ct = default)
        => _getSplits is not null
            ? _getSplits(table, session, ct)
            : Task.FromResult<IReadOnlyList<ConnectorSplit>>([ConnectorSplit.Default($"split-{ConnectorId}")]);

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadBatchAsync(
        ConnectorSplit split,
        ConnectorSessionContext session,
        CancellationToken ct = default)
        => _readBatch is not null
            ? _readBatch(split, session, ct)
            : Task.FromResult(_rows);

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadSplitAsync(
        ConnectorSplit split,
        ConnectorSessionContext session,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var batch = await ReadBatchAsync(split, session, ct).ConfigureAwait(false);
        foreach (var row in batch)
        {
            ct.ThrowIfCancellationRequested();
            yield return row;
        }
    }
}
