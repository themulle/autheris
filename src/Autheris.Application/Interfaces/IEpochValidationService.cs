namespace Autheris.Application.Interfaces;

/// <param name="Table">Table whose policy epoch is validated.</param>
/// <param name="CachedEpoch">Epoch stored with the cached entry.</param>
/// <param name="IsHighlySensitive">Sensitive tables are never served from the local staleness budget.</param>
/// <param name="IsDelegatedSource">Delegated (OBO) sources never use the staleness budget (WP-PF-07, SEC-SC-05).</param>
public record EpochCheck(TableIdentifier Table, long CachedEpoch, bool IsHighlySensitive, bool IsDelegatedSource = false);

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
