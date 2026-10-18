namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

/// <summary>
/// One request's memo of resolved attributes (V-129-13, BC-129-07): every filter and every nested read of the request sees one result per
/// attribute. An entry is discarded when it is older than <c>min(TtlSeconds, RequestMemoSeconds)</c> or when the definition hash or the
/// registry generation changed, so a principal object that outlives its request never serves stale values.
/// </summary>
internal sealed class CallerAttributeRequestState
{
    internal sealed record Entry(DateTimeOffset CreatedAt, string DefinitionHash, long Generation, Task<CallerAttributeResult> Result);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal bool TryGet(string name, out Entry entry) => _entries.TryGetValue(name, out entry!);

    internal void Set(string name, Entry entry) => _entries[name] = entry;

    internal void Remove(string name) => _entries.TryRemove(name, out _);

    private readonly object _reserveLock = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private long _reservedBinds;
    private DateTimeOffset _windowStart;

    internal CallerAttributeRequestState(TimeProvider time, TimeSpan window)
    {
        _time = time;
        _window = window;
        _windowStart = time.GetUtcNow();
    }

    /// <summary>
    /// BC-129-09: the bind items all attribute expansions of this request may use together; false when <paramref name="items"/> would exceed
    /// <paramref name="cap"/>. The sum is released with the memo window, so a principal object that outlives its request starts afresh.
    /// </summary>
    internal bool TryReserve(int items, int cap)
    {
        lock (_reserveLock)
        {
            var now = _time.GetUtcNow();
            if (now - _windowStart >= _window)
            {
                _reservedBinds = 0;
                _windowStart = now;
            }

            if (_reservedBinds + items > cap)
            {
                return false;
            }

            _reservedBinds += items;
            return true;
        }
    }
}
