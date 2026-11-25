namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Messaging;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Serialization;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using StackExchange.Redis;
using Xunit;

/// <summary>
/// Security regression tests for Redis governance pub/sub reconnect flushing, cross-node L2 consent resurrection prevention,
/// wildcard null-tenant write invalidation, and consent revocation tenant propagation.
/// </summary>
[Trait("Category", "Security")]
public sealed class Issue1097CacheInvalidationSecurityTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid UserAlice = new("S-1-5-21-1097-ALICE");
    private static readonly TableIdentifier OrdersTable = new("postgres", "public", "orders");
    private static readonly byte[] SigningKey = Encoding.UTF8.GetBytes("issue-1097-test-signing-key-32b!");

    [Fact]
    [Trait("Category", "Security")]
    public void RedisGovernanceChangeChannel_ConnectionRestored_EmitsWildcardFlushEvent_AndUnsubscribesOnDispose()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));
        var redis = new FakeRedisState();
        var mux = redis.CreateMultiplexer();
        var received = new List<GovernanceChangeEvent>();

        using var channel = new RedisGovernanceChangeChannel(mux, clock, NullLogger<RedisGovernanceChangeChannel>.Instance);
        using var sub = channel.Subscribe(received.Add);

        redis.RaiseConnectionRestored(mux);

        received.Count.ShouldBe(1);
        received[0].IsFullInvalidation.ShouldBeTrue();
        received[0].OccurredAtUtc.ShouldBe(clock.GetUtcNow());

        channel.Dispose();
        redis.ConnectionRestoredSubscribersCount.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConsentCacheService_InvalidateTenant_PreventsL2ResurrectionAcrossNodes_WithoutTouchingOtherTenants()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));
        var redis = new FakeRedisState();
        var mux = redis.CreateMultiplexer();
        var epochs = new FixedEpochService();
        using var nodeA = CreateRedisConsentCache(mux, epochs, clock);
        using var nodeB = CreateRedisConsentCache(mux, epochs, clock);

        var decisionA = TableAccessDecision.Allowed(OrdersTable, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var decisionB = TableAccessDecision.Allowed(OrdersTable, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        await nodeA.SetCachedDecisionAsync(TenantA, UserAlice, OrdersTable, decisionA, TimeSpan.FromMinutes(10));
        await nodeA.SetCachedDecisionAsync(TenantB, UserAlice, OrdersTable, decisionB, TimeSpan.FromMinutes(10));

        // Node B has an empty L1 (_tenantTables does not contain OrdersTable for TenantA).
        nodeA.InvalidateTenant(TenantA.Value);

        var resurrected = await nodeB.GetCachedDecisionAsync(TenantA, UserAlice, OrdersTable);
        resurrected.ShouldBeNull("stale L2 entries for an invalidated tenant must not resurrect into another node's L1 cache");

        // Second check confirms it was not promoted to Node B's L1 cache either.
        (await nodeB.GetCachedDecisionAsync(TenantA, UserAlice, OrdersTable)).ShouldBeNull();

        // Other tenant's cached decision remains valid.
        (await nodeB.GetCachedDecisionAsync(TenantB, UserAlice, OrdersTable)).ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConsentCacheService_InvalidateAll_AndClearL1_PreventL2ResurrectionAcrossNodes()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));
        var redis = new FakeRedisState();
        var mux = redis.CreateMultiplexer();
        var epochs = new FixedEpochService();
        using var nodeA = CreateRedisConsentCache(mux, epochs, clock);
        using var nodeB = CreateRedisConsentCache(mux, epochs, clock);

        var allow = TableAccessDecision.Allowed(OrdersTable, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        await nodeA.SetCachedDecisionAsync(TenantA, UserAlice, OrdersTable, allow, TimeSpan.FromMinutes(10));

        nodeA.InvalidateAll();

        (await nodeB.GetCachedDecisionAsync(TenantA, UserAlice, OrdersTable)).ShouldBeNull();

        // Re-populate and test async ClearL1CacheAsync.
        await nodeA.SetCachedDecisionAsync(TenantA, UserAlice, OrdersTable, allow, TimeSpan.FromMinutes(10));
        await nodeA.ClearL1CacheAsync();

        (await nodeB.GetCachedDecisionAsync(TenantA, UserAlice, OrdersTable)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConsentCacheService_InflightL2Read_InvalidatedMidFlight_DoesNotPromoteStaleDecisionIntoL1()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));
        var redis = new FakeRedisState();
        var mux = redis.CreateMultiplexer();
        var epochs = new FixedEpochService();
        using var writer = CreateRedisConsentCache(mux, epochs, clock);
        using var reader = CreateRedisConsentCache(mux, epochs, clock);

        var allow = TableAccessDecision.Allowed(OrdersTable, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        await writer.SetCachedDecisionAsync(TenantA, UserAlice, OrdersTable, allow, TimeSpan.FromMinutes(10));

        var pauseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredGet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.BeforeStringGetAsync = () =>
        {
            enteredGet.TrySetResult();
            return pauseGate.Task;
        };

        var readTask = reader.GetCachedDecisionAsync(TenantA, UserAlice, OrdersTable);
        await enteredGet.Task.WaitAsync(TimeSpan.FromSeconds(5));

        reader.InvalidateTenant(TenantA.Value);
        pauseGate.SetResult();

        var result = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        result.ShouldBeNull();

        redis.BeforeStringGetAsync = null;
        (await reader.GetCachedDecisionAsync(TenantA, UserAlice, OrdersTable)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GovernanceWriteInvalidator_InvalidateTablesAsync_WithNullTenant_BroadcastsWildcardEvent()
    {
        var notifier = new RecordingChangeNotifier();
        var target = new RecordingInvalidationTarget();
        var epochs = new FixedEpochService();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));

        var invalidator = new GovernanceWriteInvalidator(
            epochs: epochs,
            cacheInvalidators: () => [target],
            changeNotifier: notifier,
            timeProvider: clock);

        await invalidator.InvalidateTablesAsync(null, [OrdersTable]);

        notifier.Events.ShouldContain(e => e.IsFullInvalidation);
        target.InvalidateAllCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GovernanceRepository_RevokeConsentAsync_PassesResolvedTenantToWriteInvalidator()
    {
        var store = new FakeRevokeStore(OrdersTable, TenantA.Value, UserAlice.Value);
        var dialect = new PostgreSqlGovernanceDialect(() => new FakeRevokeConnection(store));
        var invalidator = new RecordingWriteInvalidator();
        var consentId = store.ConsentId;

        var repo = new GovernanceRepository(
            dialect,
            options: Options.Create(new GatewayOptions()),
            logger: NullLogger<GovernanceRepository>.Instance,
            auditAnchorStore: new InMemoryAuditChainAnchorStore(),
            grantWriter: NullFake.Create<IGrantWriter>(),
            writeInvalidator: invalidator);
        repo.InitializeAudit();

        await repo.RevokeConsentAsync(
            consentId,
            UserAlice,
            "revoke test",
            ConsentRevocationAuthority.ForVerifiedRevocation(TenantA, OrdersTable.ToString()));

        invalidator.TableCalls.Count.ShouldBe(1);
        invalidator.TableCalls[0].Tenant.ShouldBe(TenantA);
        invalidator.TableCalls[0].Tables.ShouldBe([OrdersTable]);
    }

    // Helper types after all [Fact]/[Theory] tests.

    private static ConsentCacheService CreateRedisConsentCache(
        IConnectionMultiplexer mux,
        IEpochValidationService epochs,
        TimeProvider clock)
    {
        var options = Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions
            {
                Redis = new RedisOptions { Enabled = true, InstanceName = "t1097:" }
            }
        });
        return new ConsentCacheService(
            new MemoryCache(new MemoryCacheOptions()),
            epochs,
            new InProcessChannelEventBus(),
            options,
            new MemoryPackCacheSerializer(),
            mux,
            new FixedSecretProvider(),
            NullLogger<ConsentCacheService>.Instance,
            timeProvider: clock);
    }

    private sealed class FixedSecretProvider : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretName) => SigningKey;
        public string? GetSecret(string secretName) => Convert.ToBase64String(SigningKey);
        public Task<string?> GetSecretAsync(string secretName, CancellationToken ct = default) => Task.FromResult<string?>(Convert.ToBase64String(SigningKey));
    }

    private sealed class FixedEpochService : IEpochValidationService
    {
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);
        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(tables.ToDictionary(t => t, _ => 1L));
    }

    private sealed class RecordingChangeNotifier : IGovernanceChangeNotifier
    {
        public List<GovernanceChangeEvent> Events { get; } = [];
        public Task NotifyAsync(GovernanceChangeEvent change, CancellationToken ct = default)
        {
            Events.Add(change);
            return Task.CompletedTask;
        }
        public IDisposable Subscribe(Action<GovernanceChangeEvent> handler) => DummyDisposable.Instance;
        private sealed class DummyDisposable : IDisposable
        {
            public static readonly DummyDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class RecordingInvalidationTarget : IGovernanceCacheInvalidator
    {
        public int InvalidateAllCount { get; private set; }
        public List<string> Tenants { get; } = [];
        public void InvalidateTenant(string tenantId) => Tenants.Add(tenantId);
        public void InvalidateAll() => InvalidateAllCount++;
    }

    private sealed class RecordingWriteInvalidator : IGovernanceWriteInvalidator
    {
        public List<(TenantId? Tenant, IReadOnlyList<TableIdentifier> Tables)> TableCalls { get; } = [];
        public List<(TenantId? Tenant, CatalogVersion? Version)> WideCalls { get; } = [];

        public Task InvalidateTablesAsync(
            TenantId? tenant,
            IEnumerable<TableIdentifier> tables,
            CatalogVersion? catalogVersion = null,
            Action<string>? metadataInvalidator = null,
            CancellationToken ct = default)
        {
            TableCalls.Add((tenant, tables.ToArray()));
            return Task.CompletedTask;
        }

        public Task InvalidateWideAsync(
            TenantId? tenant,
            CatalogVersion? catalogVersion = null,
            long governanceEpoch = 0,
            Guid? tableId = null,
            DateTimeOffset? occurredAtUtc = null,
            bool invalidateAccessProfiles = false,
            CancellationToken ct = default)
        {
            WideCalls.Add((tenant, catalogVersion));
            return Task.CompletedTask;
        }

        public Task PublishCatalogChangedAsync(CatalogVersion catalogVersion, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeRedisState
    {
        private readonly ConcurrentDictionary<string, RedisValue> _strings = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _hashes = new(StringComparer.Ordinal);
        private readonly List<EventHandler<ConnectionFailedEventArgs>> _restoredHandlers = [];
        private readonly object _handlerLock = new();

        public Func<Task>? BeforeStringGetAsync { get; set; }

        public int ConnectionRestoredSubscribersCount
        {
            get { lock (_handlerLock) return _restoredHandlers.Count; }
        }

        public IConnectionMultiplexer CreateMultiplexer()
        {
            var db = DispatchProxy.Create<IDatabase, DbProxy>();
            ((DbProxy)(object)db).State = this;
            var sub = DispatchProxy.Create<ISubscriber, SubProxy>();
            var mux = DispatchProxy.Create<IConnectionMultiplexer, MuxProxy>();
            var m = (MuxProxy)(object)mux;
            m.State = this;
            m.Db = db;
            m.Sub = sub;
            return mux;
        }

        public void RaiseConnectionRestored(IConnectionMultiplexer sender)
        {
            EventHandler<ConnectionFailedEventArgs>[] snapshot;
            lock (_handlerLock)
            {
                snapshot = [.. _restoredHandlers];
            }

            foreach (var h in snapshot)
            {
                h.Invoke(sender, null!);
            }
        }

        internal void AddRestored(EventHandler<ConnectionFailedEventArgs>? handler)
        {
            if (handler is null) return;
            lock (_handlerLock) _restoredHandlers.Add(handler);
        }

        internal void RemoveRestored(EventHandler<ConnectionFailedEventArgs>? handler)
        {
            if (handler is null) return;
            lock (_handlerLock) _restoredHandlers.Remove(handler);
        }

        private async Task<RedisValue> StringGetInternalAsync(RedisKey key)
        {
            if (BeforeStringGetAsync is { } hook)
            {
                await hook().ConfigureAwait(false);
            }

            return _strings.TryGetValue(key.ToString(), out var value) ? value : RedisValue.Null;
        }

        private Task<bool> StringSetInternalAsync(RedisKey key, RedisValue value)
        {
            _strings[key.ToString()] = value;
            return Task.FromResult(true);
        }

        private Task<bool> KeyDeleteInternalAsync(RedisKey key)
        {
            return Task.FromResult(_strings.TryRemove(key.ToString(), out _));
        }

        private Task<long> HashIncrementInternalAsync(RedisKey key, RedisValue field, long delta)
        {
            var bucket = _hashes.GetOrAdd(key.ToString(), static _ => new ConcurrentDictionary<string, long>(StringComparer.Ordinal));
            var next = bucket.AddOrUpdate(field.ToString(), _ => delta, (_, cur) => cur + delta);
            return Task.FromResult(next);
        }

        private Task<RedisValue[]> HashGetMultiInternalAsync(RedisKey key, RedisValue[] fields)
        {
            var bucket = _hashes.GetValueOrDefault(key.ToString());
            var result = new RedisValue[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                result[i] = bucket is not null && bucket.TryGetValue(fields[i].ToString(), out var val)
                    ? (RedisValue)val
                    : RedisValue.Null;
            }

            return Task.FromResult(result);
        }

        private Task<RedisValue> HashGetSingleInternalAsync(RedisKey key, RedisValue field)
        {
            var bucket = _hashes.GetValueOrDefault(key.ToString());
            return Task.FromResult(bucket is not null && bucket.TryGetValue(field.ToString(), out var val) ? (RedisValue)val : RedisValue.Null);
        }

#pragma warning disable CA1852
        public class MuxProxy : DispatchProxy
        {
            public FakeRedisState State { get; set; } = null!;
            public IDatabase Db { get; set; } = null!;
            public ISubscriber Sub { get; set; } = null!;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod is null) return null;
                return targetMethod.Name switch
                {
                    "get_" + nameof(IConnectionMultiplexer.IsConnected) => true,
                    nameof(IConnectionMultiplexer.GetDatabase) => Db,
                    nameof(IConnectionMultiplexer.GetSubscriber) => Sub,
                    "add_" + nameof(IConnectionMultiplexer.ConnectionRestored) => Handle(() => State.AddRestored(args?[0] as EventHandler<ConnectionFailedEventArgs>)),
                    "remove_" + nameof(IConnectionMultiplexer.ConnectionRestored) => Handle(() => State.RemoveRestored(args?[0] as EventHandler<ConnectionFailedEventArgs>)),
                    _ => DefaultFor(targetMethod.ReturnType)
                };
            }

            private static object? Handle(Action action) { action(); return null; }
        }

        public class SubProxy : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
                targetMethod is null ? null : DefaultFor(targetMethod.ReturnType);
        }

        public class DbProxy : DispatchProxy
        {
            public FakeRedisState State { get; set; } = null!;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod is null) return null;
                return targetMethod.Name switch
                {
                    nameof(IDatabase.StringGetAsync) when args is { Length: >= 1 } && args[0] is RedisKey k => State.StringGetInternalAsync(k),
                    nameof(IDatabase.StringSetAsync) when args is { Length: >= 2 } && args[0] is RedisKey k && args[1] is RedisValue v => State.StringSetInternalAsync(k, v),
                    nameof(IDatabase.KeyDeleteAsync) when args is { Length: >= 1 } && args[0] is RedisKey k => State.KeyDeleteInternalAsync(k),
                    nameof(IDatabase.HashIncrementAsync) when args is { Length: >= 3 } && args[0] is RedisKey k && args[1] is RedisValue f => State.HashIncrementInternalAsync(k, f, Convert.ToInt64(args[2])),
                    nameof(IDatabase.HashGetAsync) when args is { Length: >= 2 } && args[0] is RedisKey k && args[1] is RedisValue[] fs => State.HashGetMultiInternalAsync(k, fs),
                    nameof(IDatabase.HashGetAsync) when args is { Length: >= 2 } && args[0] is RedisKey k && args[1] is RedisValue f => State.HashGetSingleInternalAsync(k, f),
                    _ => DefaultFor(targetMethod.ReturnType)
                };
            }
        }
#pragma warning restore CA1852

        private static object? DefaultFor(Type returnType)
        {
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var t = returnType.GetGenericArguments()[0];
                var d = t.IsValueType ? Activator.CreateInstance(t) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, [d]);
            }

            return returnType.IsValueType && returnType != typeof(void) ? Activator.CreateInstance(returnType) : null;
        }
    }

    private sealed class FakeRevokeStore(TableIdentifier table, string tenantId, string granteeSid)
    {
        public Guid ConsentId { get; } = Guid.NewGuid();
        public Guid TableId { get; } = Guid.NewGuid();
        public TableIdentifier Table { get; } = table;
        public string TenantId { get; } = tenantId;
        public string GranteeSid { get; } = granteeSid;
    }

    private sealed class FakeRevokeConnection(FakeRevokeStore store) : DbConnection
    {
        [AllowNull] public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "stub";
        public override string DataSource => "stub";
        public override string ServerVersion => "0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeTx(this);
        protected override DbCommand CreateDbCommand() => new FakeCmd(this, store);

        private sealed class FakeTx(DbConnection conn) : DbTransaction
        {
            public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
            protected override DbConnection DbConnection => conn;
            public override void Commit() { }
            public override void Rollback() { }
        }

        private sealed class FakeCmd(DbConnection conn, FakeRevokeStore store) : DbCommand
        {
            private readonly FakeParams _params = new();
            [AllowNull] public override string CommandText { get; set; } = string.Empty;
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get => conn; set { } }
            protected override DbParameterCollection DbParameterCollection => _params;
            protected override DbTransaction? DbTransaction { get; set; }
            public override void Cancel() { }
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => new FakeParam();
            public override int ExecuteNonQuery() => 1;
            public override object? ExecuteScalar() =>
                CommandText.Contains("FROM TABLES", StringComparison.OrdinalIgnoreCase) ? store.TableId.ToString() :
                CommandText.Contains("SELECT COUNT", StringComparison.OrdinalIgnoreCase) ? 0L : 1L;

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                if (CommandText.Contains("FROM CONSENTS", StringComparison.OrdinalIgnoreCase))
                {
                    var now = DateTimeOffset.UtcNow;
                    object?[] row =
                    [
                        store.Table.Domain, store.Table.Schema, store.Table.TableName, store.TenantId,
                        "User", store.GranteeSid, 0, "Approval", null, null, now.AddDays(-1), now.AddDays(30),
                        store.TableId.ToString("D"), "Allow", null, store.Table.Domain
                    ];
                    return new ArrayReader(Enumerable.Range(0, row.Length).Select(i => "c" + i).ToArray(), [row]);
                }

                return new ArrayReader([], []);
            }
        }

        private sealed class FakeParam : DbParameter
        {
            public override DbType DbType { get; set; }
            public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
            public override bool IsNullable { get; set; }
            [AllowNull] public override string ParameterName { get; set; } = string.Empty;
            [AllowNull] public override string SourceColumn { get; set; } = string.Empty;
            public override object? Value { get; set; }
            public override bool SourceColumnNullMapping { get; set; }
            public override int Size { get; set; }
            public override void ResetDbType() { }
        }

        private sealed class FakeParams : DbParameterCollection
        {
            private readonly List<DbParameter> _items = [];
            public override int Count => _items.Count;
            public override object SyncRoot => _items;
            public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
            public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
            public override void Clear() => _items.Clear();
            public override bool Contains(object value) => _items.Contains((DbParameter)value);
            public override bool Contains(string value) => _items.Any(p => p.ParameterName == value);
            public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
            public override IEnumerator GetEnumerator() => _items.GetEnumerator();
            public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
            public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
            public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
            public override void Remove(object value) => _items.Remove((DbParameter)value);
            public override void RemoveAt(int index) => _items.RemoveAt(index);
            public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) RemoveAt(i); }
            protected override DbParameter GetParameter(int index) => _items[index];
            protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
            protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
            protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
        }

        private sealed class ArrayReader(string[] cols, object?[][] rows) : DbDataReader
        {
            private int _i = -1;
            public override int FieldCount => cols.Length;
            public override bool HasRows => rows.Length > 0;
            public override bool IsClosed => false;
            public override int RecordsAffected => 0;
            public override int Depth => 0;
            public override object this[int ordinal] => rows[_i][ordinal] ?? DBNull.Value;
            public override object this[string name] => this[GetOrdinal(name)];
            public override bool Read() => ++_i < rows.Length;
            public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
            public override bool NextResult() => false;
            public override bool IsDBNull(int ordinal) => rows[_i][ordinal] is null or DBNull;
            public override string GetString(int ordinal) => Convert.ToString(rows[_i][ordinal])!;
            public override int GetInt32(int ordinal) => Convert.ToInt32(rows[_i][ordinal]);
            public override long GetInt64(int ordinal) => Convert.ToInt64(rows[_i][ordinal]);
            public override object GetValue(int ordinal) => rows[_i][ordinal] ?? DBNull.Value;
            public override int GetOrdinal(string name) => Array.IndexOf(cols, name);
            public override string GetName(int ordinal) => cols[ordinal];
            public override bool GetBoolean(int ordinal) => Convert.ToBoolean(rows[_i][ordinal]);
            public override byte GetByte(int ordinal) => Convert.ToByte(rows[_i][ordinal]);
            public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
            public override char GetChar(int ordinal) => Convert.ToChar(rows[_i][ordinal]);
            public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
            public override string GetDataTypeName(int ordinal) => string.Empty;
            public override Type GetFieldType(int ordinal) => typeof(object);
            public override float GetFloat(int ordinal) => Convert.ToSingle(rows[_i][ordinal]);
            public override double GetDouble(int ordinal) => Convert.ToDouble(rows[_i][ordinal]);
            public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(rows[_i][ordinal]);
            public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
            public override short GetInt16(int ordinal) => Convert.ToInt16(rows[_i][ordinal]);
            public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(rows[_i][ordinal]);
            public override int GetValues(object[] values) => 0;
            public override IEnumerator GetEnumerator() => rows.GetEnumerator();
        }
    }
}
