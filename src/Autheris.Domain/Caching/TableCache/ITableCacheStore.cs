namespace Autheris.Domain.Caching.TableCache;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Options.TableCache;

/// <summary>
/// Physical storage engine for staging, swapping, mutating, and leasing connections to cached tables.
/// </summary>
public interface ITableCacheStore
{
    TableCacheStorageTier StorageTier { get; }

    Task InitializeTableAsync(TableCacheTargetOptions options, CancellationToken ct = default);

    Task<string> StageShadowGenerationAsync(
        TableIdentifier table,
        long epoch,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<IReadOnlyList<object?>> rows,
        CancellationToken ct = default);

    Task<TableCacheEntryMetadata> SwapActiveGenerationAsync(
        TableIdentifier table,
        long epoch,
        string shadowIdentifier,
        long rowCount,
        long sizeInBytes,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default);

    Task DropGenerationAsync(TableIdentifier table, long epoch, CancellationToken ct = default);

    Task ApplyMutationsAsync(
        TableIdentifier table,
        IReadOnlyList<TableCacheMutation> mutations,
        CancellationToken ct = default);

    Task<DbConnection> LeaseReaderConnectionAsync(
        TableIdentifier table,
        CancellationToken ct = default);

    void ReturnReaderConnection(
        TableIdentifier table,
        DbConnection connection);

    Task EvictAsync(TableIdentifier table, CancellationToken ct = default);
}
