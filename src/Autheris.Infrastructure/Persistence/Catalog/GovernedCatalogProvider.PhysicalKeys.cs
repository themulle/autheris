using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine.Governance;

namespace Autheris.Infrastructure.Persistence.Catalog;

/// <summary>
/// I403 D-3: the physical primary key and unique index directions of a table. Reuses <c>FindConnection</c>, the dialect admission
/// check, the login breaker of the connection factory and <c>_connectionFactory</c>; there is no second connection path or option
/// (I403-SC-03). Every failure yields <see cref="PhysicalKeySnapshotState.Unavailable"/>, never a partial snapshot (I403-SC-01).
/// </summary>
public sealed partial class GovernedCatalogProvider
{
    /// <summary>Hard row cap while streaming (I403-SC-10).</summary>
    private const int MaxPhysicalKeyRows = TableKeyDefinitionValidator.MaxIndexesPerTable * TableKeyDefinitionValidator.MaxColumnsPerKey;

    /// <summary>Command timeout of the physical key read (I403-SC-10); the linked caller token applies as well.</summary>
    internal TimeSpan PhysicalKeyReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async ValueTask<PhysicalKeySnapshot> ReadAsync(TableMetadata metadata, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // I403-SC-11: only SQL tables are read; the same admission as the column read (connection, provider, dialect agreement).
        if (metadata.DataSourceType != DataSourceType.Sql)
        {
            return PhysicalKeySnapshot.Unsupported;
        }

        var connectionOptions = FindConnection(metadata.Table.SourceName);
        if (connectionOptions is null ||
            !DataSourceProvider.TryResolveDialect(connectionOptions.Provider, out var dialect) ||
            metadata.Dialect != dialect ||
            !SqlDialectMapper.TryToTargetDialect(dialect, out var target))
        {
            return PhysicalKeySnapshot.Unavailable;
        }

        if (!_keyReaders.TryGetValue(target, out var reader))
        {
            return PhysicalKeySnapshot.Unsupported;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PhysicalKeyReadTimeout);
        try
        {
            await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connectionOptions, timeout.Token).ConfigureAwait(false);
            var identity = new TableIdentity(metadata.Table.SchemaName, metadata.Table.TableName);
            var rows = new List<DbKeyIndexColumn>();
            await foreach (var row in reader.ReadAsync(connection, identity, timeout.Token).ConfigureAwait(false))
            {
                if (rows.Count >= MaxPhysicalKeyRows)
                {
                    return PhysicalKeySnapshot.Unavailable;   // cap + 1 reached: abort without buffering further
                }

                rows.Add(row);
            }

            return TryBuild(rows, out var keys)
                ? new PhysicalKeySnapshot(PhysicalKeySnapshotState.Known, keys, target)
                : PhysicalKeySnapshot.Unavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogPhysicalKeyFailure(metadata.Identifier, ex);
            return PhysicalKeySnapshot.Unavailable;
        }
    }

    // Groups the streamed rows into keys. An empty result is untrustworthy (SQL Server without VIEW DEFINITION shows no indexes, I403-SC-13).
    // An ordinal gap or duplicate, a duplicate column in one key, an oversized key, too many keys or a second primary key fail closed.
    private static bool TryBuild(List<DbKeyIndexColumn> rows, out IReadOnlyList<PhysicalKey> keys)
    {
        keys = [];
        if (rows.Count == 0)
        {
            return false;
        }

        var result = new List<PhysicalKey>();
        var index = 0;
        var primaryKeys = 0;
        while (index < rows.Count)
        {
            var first = rows[index];
            var columns = new List<string>();
            var directions = new List<KeyColumnDirection>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expected = 1;
            while (index < rows.Count && string.Equals(rows[index].IndexName, first.IndexName, StringComparison.Ordinal))
            {
                var row = rows[index];
                if (row.KeyOrdinal != expected || row.IsPrimaryKey != first.IsPrimaryKey || row.IsClustered != first.IsClustered ||
                    string.IsNullOrEmpty(row.ColumnName) || !seen.Add(row.ColumnName) ||
                    columns.Count >= TableKeyDefinitionValidator.MaxColumnsPerKey)
                {
                    return false;
                }

                columns.Add(row.ColumnName);
                directions.Add(row.IsDescending ? KeyColumnDirection.Descending : KeyColumnDirection.Ascending);
                expected++;
                index++;
            }

            if (first.IsPrimaryKey && ++primaryKeys > 1)
            {
                return false;
            }

            result.Add(new PhysicalKey(first.IsPrimaryKey, first.IsClustered, columns, directions));
            if (result.Count > TableKeyDefinitionValidator.MaxIndexesPerTable)
            {
                return false;
            }
        }

        // A name that appears in two separate runs (rows not grouped by key) is an unknown shape.
        if (result.Count != rows.Select(static r => r.IndexName).Distinct(StringComparer.Ordinal).Count())
        {
            return false;
        }

        keys = result;
        return true;
    }

    // I403-SC-09: the exception type and the table id only; no message, SQL text or catalog name.
    private void LogPhysicalKeyFailure(TableIdentifier table, Exception ex)
    {
        var diagnostic = ex as SqlSourceLoginBlockedException;
        if (diagnostic is not null)
        {
            _logger.LogDebug("Physical key read skipped for table {Table}: login breaker open; no direction is applied.", table.ToQualifiedName());
            return;
        }

        _logger.LogWarning("Physical key read failed for table {Table} ({ExceptionType}); no direction is applied.",
            table.ToQualifiedName(), ex.GetType().Name);
    }
}
