namespace Autheris.Application.Interfaces;

public record EpochCheck(TableIdentifier Table, long CachedEpoch, bool IsHighlySensitive);

public interface IEpochValidationService
{
    Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default);
    Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default);
    Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default);
    Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default);
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
