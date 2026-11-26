namespace Autheris.Application.Sql.ResultCache;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autheris.Domain.Common;

/// <summary>The byte and count caps of the result cache. A total of 0 means the cache is off.</summary>
public sealed record ResultCacheLimits(long MaxTotalBytes, long MaxEntryBytes, long MaxUserBytes, int MaxRowsPerEntry, int MaxEntriesPerUser)
{
    public static ResultCacheLimits Off { get; } = new(0, 0, 0, 0, 0);

    public bool Enabled => MaxTotalBytes > 0;

    internal static ResultCacheLimits FromOptions(Autheris.Domain.Options.WebSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ResultCacheMaxMegabytes <= 0)
        {
            return Off;
        }

        const long Mb = 1024L * 1024L;
        var total = options.ResultCacheMaxMegabytes * Mb;
        return new ResultCacheLimits(
            total,
            Math.Min(Math.Max(options.ResultCacheMaxEntryMegabytes, 1) * Mb, total),
            Math.Min(Math.Max(options.ResultCacheMaxUserMegabytes, 1) * Mb, total),
            Math.Max(options.ResultCacheMaxRowsPerEntry, 1),
            Math.Max(options.ResultCacheMaxEntriesPerUser, 1));
    }
}

internal sealed class ResultCacheEntry
{
    public ResultCacheEntry(
        string hash, TenantId tenant, string userSid, IReadOnlyList<TableIdentifier> tables, CachedWebSqlResult result,
        ResultCacheEpochSnapshot snapshot, DateTimeOffset storedAt, DateTimeOffset expiresAt, string? traceId)
    {
        Hash = hash;
        Tenant = tenant;
        UserSid = userSid;
        Tables = tables;
        Result = result;
        Snapshot = snapshot;
        StoredAt = storedAt;
        ExpiresAt = expiresAt;
        TraceId = traceId;
    }

    public string Hash { get; }

    public TenantId Tenant { get; }

    public string UserSid { get; }

    public IReadOnlyList<TableIdentifier> Tables { get; }

    public CachedWebSqlResult Result { get; }

    public ResultCacheEpochSnapshot Snapshot { get; }

    public DateTimeOffset StoredAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public string? TraceId { get; }

    public long SizeBytes => Result.SizeBytes;

    internal LinkedListNode<ResultCacheEntry>? GlobalNode { get; set; }

    internal LinkedListNode<ResultCacheEntry>? UserNode { get; set; }
}

/// <summary>
/// The bounded in-memory store: a global byte LRU, a per-(tenant,user) LRU with its own byte and entry caps, in-flight recording
/// reservations and bounded tombstones. All state sits behind one lock and nothing here awaits or calls out.
/// </summary>
internal sealed class WebSqlResultCacheStore : IDisposable
{
    internal const int DefaultMaxTombstones = 10_000;

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly int _maxTombstones;
    private readonly Dictionary<string, ResultCacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<ResultCacheEntry> _lru = new();
    private readonly Dictionary<(string Tenant, string Sid), UserState> _users = [];
    private readonly Dictionary<string, LinkedListNode<Tombstone>> _tombstones = new(StringComparer.Ordinal);
    private readonly LinkedList<Tombstone> _tombstoneLru = new();
    private readonly IDisposable _metricsRegistration;
    private ResultCacheLimits _limits = ResultCacheLimits.Off;
    private long _totalBytes;
    private long _reservedBytes;
    private long _entryCount;

    public WebSqlResultCacheStore(TimeProvider? timeProvider = null, int maxTombstones = DefaultMaxTombstones)
    {
        _time = timeProvider ?? TimeProvider.System;
        _maxTombstones = Math.Max(1, maxTombstones);
        _metricsRegistration = WebSqlResultCacheMetrics.Register(() => (Volatile.Read(ref _totalBytes), Volatile.Read(ref _entryCount)));
    }

    public long TotalBytes
    {
        get { lock (_gate) { return _totalBytes; } }
    }

    public long ReservedBytes
    {
        get { lock (_gate) { return _reservedBytes; } }
    }

    public int EntryCount
    {
        get { lock (_gate) { return _entries.Count; } }
    }

    public int TombstoneCount
    {
        get { lock (_gate) { return _tombstones.Count; } }
    }

    public void Configure(ResultCacheLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        lock (_gate)
        {
            _limits = limits;
            if (!limits.Enabled)
            {
                ClearCore();
                return;
            }

            while (_totalBytes > limits.MaxTotalBytes && _lru.Last is { } last)
            {
                Remove(last.Value, WebSqlResultCacheInvalidationReasons.Evicted);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearCore();
        }
    }

    /// <summary>The entry for the hash, only when tenant and user match and it has not expired. A hit does not extend its lifetime.</summary>
    public ResultCacheEntry? TryGet(string hash, TenantId tenant, string userSid)
    {
        lock (_gate)
        {
            if (!_limits.Enabled || !_entries.TryGetValue(hash, out var entry))
            {
                return null;
            }

            if (entry.Tenant != tenant || !string.Equals(entry.UserSid, userSid, StringComparison.Ordinal))
            {
                Remove(entry, WebSqlResultCacheInvalidationReasons.Evicted);
                return null;
            }

            if (_time.GetUtcNow() >= entry.ExpiresAt)
            {
                Remove(entry, WebSqlResultCacheInvalidationReasons.Ttl);
                return null;
            }

            _lru.Remove(entry.GlobalNode!);
            _lru.AddFirst(entry.GlobalNode!);
            var user = _users[(entry.Tenant.Value, entry.UserSid)];
            user.Lru.Remove(entry.UserNode!);
            user.Lru.AddFirst(entry.UserNode!);
            return entry;
        }
    }

    public void Invalidate(ResultCacheEntry entry, string reason)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Hash, out var current) && ReferenceEquals(current, entry))
            {
                Remove(entry, reason);
            }
        }
    }

    /// <summary>
    /// Stores the entry, replacing a previous one for the same hash only when the new one is accepted. Evicts the user's own oldest
    /// entries to meet the user caps first, then least recently used entries of anybody to meet the global cap.
    /// </summary>
    public bool TryPut(ResultCacheEntry entry, IResultCacheReservation? committing = null)
    {
        lock (_gate)
        {
            var limits = _limits;
            var size = entry.SizeBytes;

            // The committing recording's own reservation stands for the memory this entry is about to occupy, so it is not counted twice.
            var otherReserved = _reservedBytes - (committing is Reservation mine && ReferenceEquals(mine.Store, this) ? mine.Held : 0);
            if (!limits.Enabled || size > limits.MaxEntryBytes || size > limits.MaxUserBytes || size + otherReserved > limits.MaxTotalBytes)
            {
                return false;
            }

            if (_entries.TryGetValue(entry.Hash, out var previous))
            {
                RemoveCore(previous);
            }

            var userKey = (entry.Tenant.Value, entry.UserSid);
            if (!_users.TryGetValue(userKey, out var user))
            {
                user = new UserState();
                _users[userKey] = user;
            }

            while ((user.Count >= limits.MaxEntriesPerUser || user.Bytes + size > limits.MaxUserBytes) && user.Lru.Last is { } own)
            {
                Remove(own.Value, WebSqlResultCacheInvalidationReasons.UserCap);
            }

            while (_totalBytes + otherReserved + size > limits.MaxTotalBytes && _lru.Last is { } oldest)
            {
                Remove(oldest.Value, WebSqlResultCacheInvalidationReasons.Evicted);
            }

            if (_totalBytes + otherReserved + size > limits.MaxTotalBytes)
            {
                DropIfEmpty(userKey, user);
                return false;
            }

            _users[userKey] = user;
            entry.GlobalNode = _lru.AddFirst(entry);
            entry.UserNode = user.Lru.AddFirst(entry);
            _entries[entry.Hash] = entry;
            user.Bytes += size;
            user.Count++;
            _totalBytes += size;
            Volatile.Write(ref _entryCount, _entries.Count);
            return true;
        }
    }

    /// <summary>Starts a reservation for a recording of the user. Null when the cache is off.</summary>
    public IResultCacheReservation? BeginReservation(TenantId tenant, string userSid)
    {
        lock (_gate)
        {
            if (!_limits.Enabled)
            {
                return null;
            }

            var userKey = (tenant.Value, userSid);
            if (!_users.TryGetValue(userKey, out var user))
            {
                user = new UserState();
                _users[userKey] = user;
            }

            user.Recordings++;
            return new Reservation(this, userKey, user);
        }
    }

    public void MarkTooLarge(string hash, TimeSpan ttl)
    {
        lock (_gate)
        {
            if (!_limits.Enabled || ttl <= TimeSpan.Zero)
            {
                return;
            }

            var expires = _time.GetUtcNow() + ttl;
            if (_tombstones.TryGetValue(hash, out var existing))
            {
                _tombstoneLru.Remove(existing);
            }

            _tombstones[hash] = _tombstoneLru.AddFirst(new Tombstone(hash, expires));
            while (_tombstones.Count > _maxTombstones && _tombstoneLru.Last is { } last)
            {
                _tombstones.Remove(last.Value.Hash);
                _tombstoneLru.RemoveLast();
            }
        }
    }

    public bool IsMarkedTooLarge(string hash)
    {
        lock (_gate)
        {
            if (!_limits.Enabled || !_tombstones.TryGetValue(hash, out var node))
            {
                return false;
            }

            if (_time.GetUtcNow() >= node.Value.ExpiresAt)
            {
                _tombstones.Remove(hash);
                _tombstoneLru.Remove(node);
                return false;
            }

            return true;
        }
    }

    public void Dispose() => _metricsRegistration.Dispose();

    private bool TryGrow(Reservation reservation, long bytes)
    {
        lock (_gate)
        {
            var user = reservation.User;
            var limits = _limits;
            if (reservation.Disposed || !limits.Enabled || bytes <= 0 || user.Bytes + user.Reserved + bytes > limits.MaxUserBytes)
            {
                return false;
            }

            // A recording may still fail, so it never evicts other users' entries; it only makes room from its own user's entries.
            while (_totalBytes + _reservedBytes + bytes > limits.MaxTotalBytes && user.Lru.Last is { } own)
            {
                Remove(own.Value, WebSqlResultCacheInvalidationReasons.UserCap);
            }

            if (_totalBytes + _reservedBytes + bytes > limits.MaxTotalBytes)
            {
                return false;
            }

            user.Reserved += bytes;
            _reservedBytes += bytes;
            reservation.Held += bytes;
            return true;
        }
    }

    private void Release(Reservation reservation)
    {
        lock (_gate)
        {
            if (reservation.Disposed)
            {
                return;
            }

            reservation.Disposed = true;
            reservation.User.Reserved -= reservation.Held;
            _reservedBytes -= reservation.Held;
            reservation.Held = 0;
            reservation.User.Recordings--;
            DropIfEmpty(reservation.UserKey, reservation.User);
        }
    }

    private void Remove(ResultCacheEntry entry, string reason)
    {
        RemoveCore(entry);
        WebSqlResultCacheMetrics.RecordInvalidation(reason);
    }

    private void RemoveCore(ResultCacheEntry entry)
    {
        _entries.Remove(entry.Hash);
        _lru.Remove(entry.GlobalNode!);
        var userKey = (entry.Tenant.Value, entry.UserSid);
        var user = _users[userKey];
        user.Lru.Remove(entry.UserNode!);
        user.Bytes -= entry.SizeBytes;
        user.Count--;
        _totalBytes -= entry.SizeBytes;
        Volatile.Write(ref _entryCount, _entries.Count);
        DropIfEmpty(userKey, user);
    }

    private void DropIfEmpty((string Tenant, string Sid) userKey, UserState user)
    {
        if (user.Count == 0 && user.Recordings == 0)
        {
            _users.Remove(userKey);
        }
    }

    private void ClearCore()
    {
        _entries.Clear();
        _lru.Clear();
        foreach (var (key, user) in _users.ToArray())
        {
            user.Lru.Clear();
            user.Bytes = 0;
            user.Count = 0;
            DropIfEmpty(key, user);
        }

        _tombstones.Clear();
        _tombstoneLru.Clear();
        _totalBytes = 0;
        Volatile.Write(ref _entryCount, 0);
    }

    private sealed class UserState
    {
        public LinkedList<ResultCacheEntry> Lru { get; } = new();

        public long Bytes { get; set; }

        public long Reserved { get; set; }

        public int Count { get; set; }

        public int Recordings { get; set; }
    }

    private sealed record Tombstone(string Hash, DateTimeOffset ExpiresAt);

    private sealed class Reservation(WebSqlResultCacheStore store, (string Tenant, string Sid) userKey, UserState user) : IResultCacheReservation
    {
        public WebSqlResultCacheStore Store { get; } = store;

        public (string Tenant, string Sid) UserKey { get; } = userKey;

        public UserState User { get; } = user;

        // Both fields are only read and written under the store lock.
        public long Held { get; set; }

        public bool Disposed { get; set; }

        public bool TryGrow(long bytes) => Store.TryGrow(this, bytes);

        public void Dispose() => Store.Release(this);
    }
}
