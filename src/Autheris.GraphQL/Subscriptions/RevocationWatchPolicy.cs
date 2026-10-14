namespace Autheris.GraphQL.Subscriptions;

using System;
using System.Threading;
using Microsoft.Extensions.Options;

/// <summary>
/// SR-L-18: effective fail-closed policy of the revocation watchers (WebSocket sessions and subscription streams).
/// </summary>
public sealed record RevocationWatchPolicy(int MaxConsecutiveFailures, bool FailClosed)
{
    public static RevocationWatchPolicy Default { get; } =
        new(RevocationWatchOptions.DefaultMaxConsecutiveFailures, true);

    public static RevocationWatchPolicy From(RevocationWatchOptions? options)
        => options == null
            ? Default
            : new RevocationWatchPolicy(Math.Max(1, options.RevocationCheckMaxConsecutiveFailures), options.RevocationCheckFailClosed);

    public static RevocationWatchPolicy From(IOptions<RevocationWatchOptions>? options) => From(options?.Value);
}

/// <summary>
/// SR-L-18: counts consecutive revocation check failures so that both watchers behave identically. Thread-safe.
/// </summary>
public sealed class RevocationFailureCounter
{
    private readonly RevocationWatchPolicy _policy;
    private int _consecutiveFailures;

    public RevocationFailureCounter(RevocationWatchPolicy? policy = null)
    {
        _policy = policy ?? RevocationWatchPolicy.Default;
    }

    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>
    /// Records a failed check. Returns true when the session must be terminated (fail-closed and the threshold of
    /// consecutive failures is reached).
    /// </summary>
    public bool RecordFailure()
    {
        var count = Interlocked.Increment(ref _consecutiveFailures);
        return _policy.FailClosed && count >= _policy.MaxConsecutiveFailures;
    }

    /// <summary>Resets the counter after a successful check.</summary>
    public void RecordSuccess() => Volatile.Write(ref _consecutiveFailures, 0);
}
