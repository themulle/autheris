namespace Autheris.Application.Interfaces;

/// <param name="Table">Table whose policy epoch is validated.</param>
/// <param name="CachedEpoch">Epoch stored with the cached entry.</param>
/// <param name="IsHighlySensitive">Sensitive tables are never served from the local staleness budget.</param>
/// <param name="IsDelegatedSource">Delegated (OBO) sources never use the staleness budget (WP-PF-07, SEC-SC-05).</param>
public record EpochCheck(TableIdentifier Table, long CachedEpoch, bool IsHighlySensitive, bool IsDelegatedSource = false);

public interface IEpochValidationService
{
    Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default);

    /// <summary>
    /// Single-table validation that honours <see cref="EpochCheck.IsDelegatedSource"/> (WP-PF-07, SEC-SC-05): a delegated (OBO) source gets zero staleness budget.
    /// TODO(callers): <c>ConsentCacheService</c> still validates with the overload above because the cached envelope does not carry the delegation flag; pass it once it does.
    /// </summary>
    async Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, bool isDelegatedSource, CancellationToken ct = default)
    {
        if (!isDelegatedSource)
        {
            return await IsEpochValidAsync(table, cachedEpoch, ct).ConfigureAwait(false);
        }

        var results = await AreEpochsValidAsync([new EpochCheck(table, cachedEpoch, false, IsDelegatedSource: true)], ct).ConfigureAwait(false);
        return results.TryGetValue(table, out var valid) && valid;
    }

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
