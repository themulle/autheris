namespace TrinoSqlEngine.Parsing;

using System;
using System.Collections.Concurrent;
using System.Linq;

/// <summary>
/// ARC-S-09: small bounded cache of parser syntax failures so that repeated invalid input is rejected without lexing and parsing it
/// again. Only syntax failures of the parser are stored (never a success, a timeout, a cancellation or a policy decision). The key is
/// the full SQL text, the entry rule, the token security options and the structural limits; a hit compares the complete text, so a
/// hash collision can never reject a different statement.
/// </summary>
internal sealed class SyntaxErrorNegativeCache
{
    /// <summary>Statements longer than this are never stored (bounds the memory per entry).</summary>
    internal const int MaxCachedSqlLength = 4096;

    private readonly ConcurrentDictionary<Key, Entry> _entries = new();

    private readonly record struct Key(int TextHash, int RuleKind, int OptionsHash);

    private sealed record Entry(string Sql, SqlTokenSecurityOptions Options, int Limits, string Message, long ExpiresAtTicks);

    public int Count => _entries.Count;

    public bool TryGet(ReadOnlySpan<char> sql, int ruleKind, SqlTokenSecurityOptions options, int limits, out string message)
    {
        message = string.Empty;
        if (_entries.IsEmpty || sql.Length > MaxCachedSqlLength)
        {
            return false;
        }

        var key = new Key(string.GetHashCode(sql), ruleKind, options.GetHashCode());
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (Environment.TickCount64 >= entry.ExpiresAtTicks)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        if (entry.Limits != limits || !entry.Options.Equals(options) || !sql.SequenceEqual(entry.Sql.AsSpan()))
        {
            return false;
        }

        message = entry.Message;
        return true;
    }

    public void Add(ReadOnlySpan<char> sql, int ruleKind, SqlTokenSecurityOptions options, int limits, string message, TimeSpan ttl, int maxEntries)
    {
        if (ttl <= TimeSpan.Zero || maxEntries <= 0 || sql.Length > MaxCachedSqlLength)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (_entries.Count >= maxEntries)
        {
            foreach (var expired in _entries.Where(e => now >= e.Value.ExpiresAtTicks).Select(e => e.Key).ToList())
            {
                _entries.TryRemove(expired, out _);
            }

            if (_entries.Count >= maxEntries)
            {
                return; // full of live entries: stay bounded, the next failure is simply parsed again
            }
        }

        var key = new Key(string.GetHashCode(sql), ruleKind, options.GetHashCode());
        _entries[key] = new Entry(sql.ToString(), options, limits, message, now + (long)ttl.TotalMilliseconds);
    }

    public void Clear() => _entries.Clear();
}
