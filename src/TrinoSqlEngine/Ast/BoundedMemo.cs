namespace TrinoSqlEngine.Ast;

using System;
using System.Collections.Concurrent;

/// <summary>
/// Memoizes a pure function of a string (type name parsing). The key space can contain request text, so the table is bounded: once it
/// is full, new keys are computed but not stored (no growth under hostile input). Only successful results are stored; a function that
/// throws (fail closed validation) is re-evaluated every time. Never use it for anything that depends on a request value or a policy.
/// </summary>
internal sealed class BoundedMemo<TValue>
{
    private readonly ConcurrentDictionary<string, TValue> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;

    public BoundedMemo(int capacity = 512)
    {
        _capacity = capacity;
    }

    public TValue GetOrCompute(string key, Func<string, TValue> compute)
    {
        if (_entries.TryGetValue(key, out var hit))
        {
            return hit;
        }

        var value = compute(key);
        if (_entries.Count < _capacity)
        {
            _entries.TryAdd(key, value);
        }

        return value;
    }
}
