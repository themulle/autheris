namespace Autheris.Application.State;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Result of a cluster state read that distinguishes "found", "absent" and "unavailable" without exceptions.
/// </summary>
public readonly record struct StateRead<T>(bool Found, T? Value, bool Unavailable)
{
    public static StateRead<T> FoundValue(T value) => new(true, value, false);

    public static StateRead<T> Absent { get; } = new(false, default, false);

    public static StateRead<T> StoreUnavailable { get; } = new(false, default, true);
}

public static class DistributedClusterStateProviderExtensions
{
    /// <summary>
    /// Reads a key and reports store failures as <see cref="StateRead{T}.Unavailable"/> instead of throwing
    /// <see cref="ClusterStateUnavailableException"/>. Cancellation still propagates.
    /// </summary>
    public static async ValueTask<StateRead<T>> GetOrUnavailableAsync<T>(
        this IDistributedClusterStateProvider provider,
        string key,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        try
        {
            var value = await provider.GetAsync<T>(key, ct).ConfigureAwait(false);
            return value is null ? StateRead<T>.Absent : StateRead<T>.FoundValue(value);
        }
        catch (ClusterStateUnavailableException)
        {
            return StateRead<T>.StoreUnavailable;
        }
    }
}
