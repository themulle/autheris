namespace Autheris.Application.Policy.Services;

using System;
using System.Collections.Generic;
using System.Threading;

/// <summary>
/// Issue #1044: a bounded record of the policy invalidations this process has applied (its own writes and those announced by peers).
/// Every <see cref="Append"/> advances <see cref="Generation"/>. A reader that cached something at an older generation asks which
/// tables were invalidated since then. The answer is null whenever the log cannot prove it: the range fell out of the ring, the
/// generation is not one of this log, or an invalidation of every table (<see cref="AllTables"/>) lies in between. The reader then
/// rebuilds what it cached (fail closed).
/// </summary>
public sealed class PolicyInvalidationLog
{
    /// <summary>The key of an invalidation that may affect any table (catalog version, pattern consents, access profiles, reconnect).</summary>
    public const string AllTables = "*";

    public const int DefaultCapacity = 4096;

    private readonly Lock _lock = new();
    private readonly (long Generation, string Key)[] _ring;
    private long _generation;

    public PolicyInvalidationLog(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _ring = new (long, string)[capacity];
    }

    /// <summary>The number of invalidations appended so far.</summary>
    public long Generation
    {
        get
        {
            lock (_lock)
            {
                return _generation;
            }
        }
    }

    /// <summary>Records the invalidation of <paramref name="key"/> (a lower-case table key, or <see cref="AllTables"/>).</summary>
    public void Append(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_lock)
        {
            _generation++;
            _ring[_generation % _ring.Length] = (_generation, key.ToLowerInvariant());
        }
    }

    /// <summary>
    /// The table keys invalidated after <paramref name="generation"/>, or null when the log cannot prove the complete set
    /// (see the class summary). An empty set means nothing was invalidated since.
    /// </summary>
    public IReadOnlySet<string>? InvalidatedSince(long generation)
    {
        lock (_lock)
        {
            if (generation < 0 || generation > _generation || _generation - generation > _ring.Length)
            {
                return null;
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var g = generation + 1; g <= _generation; g++)
            {
                var (recorded, key) = _ring[g % _ring.Length];
                if (recorded != g || key == AllTables)
                {
                    return null;
                }

                keys.Add(key);
            }

            return keys;
        }
    }
}
