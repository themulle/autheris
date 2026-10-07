namespace Autheris.Application.Interfaces;

/// <summary>
/// O10: Bounds concurrent table reads per key. <see cref="TryEnter"/> returns a lease that frees the slot on dispose, or
/// null when <paramref name="maxConcurrent"/> reads with this key are already running. A limit of 0 or less is unlimited.
/// </summary>
public interface ITableReadConcurrencyGate
{
    IDisposable? TryEnter(string key, int maxConcurrent);
}
