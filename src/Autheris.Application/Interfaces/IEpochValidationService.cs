namespace Autheris.Application.Interfaces;

public record EpochCheck(TableIdentifier Table, long CachedEpoch, bool IsHighlySensitive);

public interface IEpochValidationService
{
    Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default);
    Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default);
    Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default);
    Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default);
    /// <summary>
    /// Issue #107 (SEC-RF107-05): all-or-nothing authoritative epoch read. Null when Redis is configured but not connected, when any read
    /// fails or when not every table can be confirmed; never throws. The default is null (not authoritative, callers fall back to exact evaluation).
    /// </summary>
    ValueTask<IReadOnlyDictionary<TableIdentifier, long>?> TryGetAuthoritativeEpochsAsync(
        IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyDictionary<TableIdentifier, long>?>(null);

    ValueTask<IReadOnlyDictionary<TableIdentifier, bool>> AreEpochsValidAsync(IReadOnlyList<EpochCheck> checks, CancellationToken ct = default)
    {
        var dict = new Dictionary<TableIdentifier, bool>();
        foreach (var check in checks)
        {
            dict[check.Table] = true;
        }
        return ValueTask.FromResult<IReadOnlyDictionary<TableIdentifier, bool>>(dict);
    }
}
