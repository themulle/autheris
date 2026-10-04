namespace Autheris.Application.Interfaces;

public interface IEpochValidationService
{
    Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default);
    Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default);
    Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default);
    Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default);
}
