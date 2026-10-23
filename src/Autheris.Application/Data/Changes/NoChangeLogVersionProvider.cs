namespace Autheris.Application.Data.Changes;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// VF-13: the production default of <see cref="IChangeVersionProvider"/> until a real change-log provider exists. Every table reports "no change
/// tracking", so <c>changesSince</c> and the change token answer <see cref="ChangeDetectionErrorCodes.ChangeTrackingNotSupported"/> instead of
/// returning empty deltas and advancing a cursor past changes that were never read.
/// </summary>
public sealed class NoChangeLogVersionProvider : IChangeVersionProvider
{
    public bool ProvidesPreImages => false;

    public ValueTask<long?> GetCurrentVersionAsync(TableIdentifier table, CancellationToken ct = default) => ValueTask.FromResult<long?>(null);

    public ValueTask<long?> GetMinValidVersionAsync(TableIdentifier table, CancellationToken ct = default) => ValueTask.FromResult<long?>(null);

    public Task<IReadOnlyList<SourceChangeRecord>> GetChangesBetweenAsync(
        TableIdentifier table, long sinceVersion, long upToVersion, int maxRecords, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SourceChangeRecord>>(Array.Empty<SourceChangeRecord>());

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadCurrentRowsByPrimaryKeysAsync(
        TableIdentifier table, IReadOnlyList<string> primaryKeyColumns, IReadOnlyList<CompositeKey> keys, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Array.Empty<IReadOnlyDictionary<string, object?>>());
}
