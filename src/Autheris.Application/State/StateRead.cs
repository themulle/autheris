namespace Autheris.Application.State;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Result of a cluster state read that distinguishes "found", "absent" and "unavailable" without exceptions.
/// </summary>
public readonly record struct StateRead<T>(bool Found, T? Value, bool Unavailable);

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
            return value is null ? new StateRead<T>(false, default, false) : new StateRead<T>(true, value, false);
        }
        catch (ClusterStateUnavailableException)
        {
            return new StateRead<T>(false, default, true);
        }
    }
}
