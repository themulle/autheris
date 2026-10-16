namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

/// <summary>A loaded value with its absolute expiration and estimated size in bytes.</summary>
public sealed record SingleFlightLoadResult<TValue>(TValue Value, DateTimeOffset AbsoluteExpiration, long SizeBytes) where TValue : class;

/// <summary>
/// Issue #107 (PLAN-GOV-107 A-5): generic dedicated, size-limited, single-flight cache extracted from <c>VirtualFilterKeySetCache</c>:
/// single flight per key, failures never cached, shared load detached from caller cancellation and bounded by its own timeout,
/// absolute expiration only. WP-C owns the implementation; the member set is the agreed contract.
/// </summary>
public sealed class SingleFlightKeySetCache<TValue> : IDisposable where TValue : class
{
    public SingleFlightKeySetCache(long sizeLimitBytes, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeLimitBytes);
    }

    public long CurrentEstimatedSize => throw new NotImplementedException("WP-C: SingleFlightKeySetCache");

    public bool TryGet(string key, out TValue? value)
        => throw new NotImplementedException("WP-C: SingleFlightKeySetCache");

    public void Remove(string key)
        => throw new NotImplementedException("WP-C: SingleFlightKeySetCache");

    public ValueTask<TValue> GetOrLoadAsync(
        string key,
        Func<CancellationToken, Task<SingleFlightLoadResult<TValue>>> load,
        CancellationToken ct,
        TimeSpan? loadTimeout = null)
        => throw new NotImplementedException("WP-C: SingleFlightKeySetCache");

    public void Dispose()
    {
    }
}
