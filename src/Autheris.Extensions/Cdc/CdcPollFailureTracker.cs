namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Concurrent;
using Autheris.Domain.Common;

/// <summary>
/// Remembers tables whose polling failed with a persistent error (failed activation, invalid configuration) so the producer backs
/// off exponentially and logs the failure once per streak instead of every polling cycle.
/// </summary>
public sealed class CdcPollFailureTracker(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<TableIdentifier, (int Failures, DateTimeOffset NextAttempt)> _state = new();

    public bool ShouldSkip(TableIdentifier table) =>
        _state.TryGetValue(table, out var entry) && _time.GetUtcNow() < entry.NextAttempt;

    /// <summary>Records a persistent failure; returns true for the first failure of a streak (the one to log).</summary>
    public bool RecordFailure(TableIdentifier table)
    {
        var first = false;
        _state.AddOrUpdate(
            table,
            _ =>
            {
                first = true;
                return (1, _time.GetUtcNow() + TimeSpan.FromSeconds(5));
            },
            (_, existing) =>
            {
                var failures = existing.Failures + 1;
                var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 5 * Math.Pow(2, Math.Min(failures, 10))));
                return (failures, _time.GetUtcNow() + delay);
            });
        return first;
    }

    public void RecordSuccess(TableIdentifier table) => _state.TryRemove(table, out _);
}
