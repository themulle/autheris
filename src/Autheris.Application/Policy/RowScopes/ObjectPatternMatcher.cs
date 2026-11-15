using System.Collections.Concurrent;
using Autheris.Domain.Model;

namespace Autheris.Application.Policy.RowScopes;

/// <summary>
/// Compiles and evaluates strict three-segment pattern grant expressions (<c>data_source.schema.object</c>).
/// Requires a literal <c>data_source</c> and permits <c>*</c> glob wildcards only in schema and object segments.
/// </summary>
public sealed class ObjectPatternMatcher
{
    private readonly ObjectPatternSpec _spec;

    private ObjectPatternMatcher(ObjectPatternSpec spec)
    {
        _spec = spec;
    }

    public string NormalizedPattern => _spec.NormalizedPattern;
    public string DataSource => _spec.DataSource;
    public string SchemaPattern => _spec.SchemaPattern;
    public string ObjectNamePattern => _spec.ObjectNamePattern;

    public static bool TryParse(string? pattern, out ObjectPatternMatcher? matcher, out string error)
    {
        if (!ObjectPatternSpec.TryParse(pattern, out var spec, out error))
        {
            matcher = null;
            return false;
        }

        matcher = new ObjectPatternMatcher(spec!);
        return true;
    }

    public static ObjectPatternMatcher Parse(string pattern) =>
        new(ObjectPatternSpec.Parse(pattern));

    public bool Matches(TableIdentifier table) => _spec.Matches(table);

    public bool Matches(TableMetadata tableMetadata)
    {
        ArgumentNullException.ThrowIfNull(tableMetadata);
        if (tableMetadata.Table.DiscoveryHidden)
        {
            return false;
        }

        return _spec.Matches(tableMetadata.Identifier);
    }

    public bool Matches(string dataSource, string schemaName, string objectName) =>
        _spec.Matches(dataSource, schemaName, objectName);
}

/// <summary>
/// Thread-safe cache of compiled <see cref="ObjectPatternMatcher"/> instances partitioned by catalog epoch.
/// Automatically discards entries from older epochs so cached coverage sets turn over on catalog changes.
/// </summary>
public sealed class PatternEpochMatcherCache
{
    private const int MaxEntriesPerEpoch = 4096;
    private long _currentEpoch = long.MinValue;
    private ConcurrentDictionary<string, ObjectPatternMatcher> _matchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _epochLock = new();

    public ObjectPatternMatcher? GetOrParse(long catalogEpoch, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        var map = EnsureEpochMap(catalogEpoch);
        if (map.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        if (!ObjectPatternMatcher.TryParse(pattern, out var parsed, out _))
        {
            return null;
        }

        if (map.Count < MaxEntriesPerEpoch)
        {
            map.TryAdd(pattern, parsed!);
        }

        return parsed;
    }

    private ConcurrentDictionary<string, ObjectPatternMatcher> EnsureEpochMap(long catalogEpoch)
    {
        if (Volatile.Read(ref _currentEpoch) == catalogEpoch)
        {
            return _matchers;
        }

        lock (_epochLock)
        {
            if (_currentEpoch != catalogEpoch)
            {
                _matchers = new ConcurrentDictionary<string, ObjectPatternMatcher>(StringComparer.OrdinalIgnoreCase);
                Volatile.Write(ref _currentEpoch, catalogEpoch);
            }

            return _matchers;
        }
    }
}
