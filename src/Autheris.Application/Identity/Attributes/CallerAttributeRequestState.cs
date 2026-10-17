namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Concurrent;
using System.Threading;
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

    private long _reservedBinds;

    /// <summary>BC-129-09: the bind items all attribute expansions of this request may use together; false when <paramref name="items"/> would exceed <paramref name="cap"/>.</summary>
    internal bool TryReserve(int items, int cap)
    {
        while (true)
        {
            long current = Interlocked.Read(ref _reservedBinds);
            if (current + items > cap)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _reservedBinds, current + items, current) == current)
            {
                return true;
            }
        }
    }
}
