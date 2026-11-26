namespace Autheris.Tests.Unit.Sql.ResultCache;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Sql;
using Autheris.Application.Sql.ResultCache;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

internal sealed class FakeEpochService : IEpochValidationService
{
    public long? Generation { get; set; } = 1;

    public long? Cluster { get; set; } = 0;

    public IReadOnlySet<string>? Invalidated { get; set; } = new HashSet<string>();

    public Dictionary<TableIdentifier, long>? Epochs { get; set; } = new();

    public bool ThrowOnRead { get; set; }

    public long? InvalidationGeneration => Generation;

    public IReadOnlySet<string>? GetInvalidatedSince(long generation) => Invalidated;

    public ValueTask<long?> GetClusterInvalidationSequenceAsync(CancellationToken ct = default) =>
        ThrowOnRead ? throw new InvalidOperationException("boom") : ValueTask.FromResult(Cluster);

    public ValueTask<IReadOnlyDictionary<TableIdentifier, long>?> TryGetAuthoritativeEpochsAsync(
        IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
    {
        if (ThrowOnRead)
        {
            throw new InvalidOperationException("boom");
        }

        if (Epochs is null)
        {
            return ValueTask.FromResult<IReadOnlyDictionary<TableIdentifier, long>?>(null);
        }

        var result = new Dictionary<TableIdentifier, long>();
        foreach (var table in tables)
        {
            result[table] = Epochs.GetValueOrDefault(table, 1);
        }

        return ValueTask.FromResult<IReadOnlyDictionary<TableIdentifier, long>?>(result);
    }

    public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);

    public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;

    public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);

    public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(new Dictionary<TableIdentifier, long>());
}

internal sealed class MutableEpochStore : IGovernanceEpochStore
{
    public long Epoch { get; set; } = 1;

    public bool Throw { get; set; }

    public Task<long> GetEpochAsync(TenantId tenant, CancellationToken ct = default) =>
        Throw ? throw new InvalidOperationException("boom") : Task.FromResult(Epoch);

    public Task<long> BumpEpochAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(++Epoch);
}

internal sealed class CacheHarness : IDisposable
{
    public static readonly TenantId Tenant = new("t1");

    public static readonly TableIdentifier Orders = new("main", "dbo", "Orders");

    public CacheHarness(int megabytes = 16, bool withEpochServices = true, int maxUserMegabytes = 8, int maxEntriesPerUser = 100)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Options = new StaticOptionsMonitor<GatewayOptions>(Create(megabytes, maxUserMegabytes, maxEntriesPerUser));
        Governance = new GovernanceEpochProvider(Store, Microsoft.Extensions.Options.Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }));
        Cache = new WebSqlResultCache(Options, withEpochServices ? Governance : null, withEpochServices ? Epochs : null, Time);
    }

    public FakeTimeProvider Time { get; }

    public StaticOptionsMonitor<GatewayOptions> Options { get; }

    public MutableEpochStore Store { get; } = new();

    public FakeEpochService Epochs { get; } = new();

    public GovernanceEpochProvider Governance { get; }

    public WebSqlResultCache Cache { get; }

    public static GatewayOptions Create(int megabytes, int maxUserMegabytes = 8, int maxEntriesPerUser = 100) => new()
    {
        WebSql = new WebSqlOptions
        {
            ResultCacheMaxMegabytes = megabytes,
            ResultCacheMaxEntryMegabytes = 1,
            ResultCacheMaxUserMegabytes = maxUserMegabytes,
            ResultCacheMaxRowsPerEntry = 1000,
            ResultCacheMaxEntriesPerUser = maxEntriesPerUser,
        },
    };

    public void BumpGovernance()
    {
        Store.Epoch++;
        Governance.InvalidateTenant(Tenant.Value);
    }

    public static WebSqlResultCacheKey Key(string hash = "h1", string sid = "S-1") => new(hash, Tenant, sid, [Orders]);

    public void Dispose()
    {
        Cache.Dispose();
        Governance.Dispose();
    }
}

internal static class ResultCacheFixtures
{
    public static readonly GovernedResultInfo Info = new([], ImmutableArray<bool>.Empty);

    public static CachedWebSqlResult Result(int rows = 2, long sizeBytes = 1000, bool truncated = false)
    {
        var data = new object?[rows][];
        for (var i = 0; i < rows; i++)
        {
            data[i] = [i, $"name{i}"];
        }

        return new CachedWebSqlResult(Info, ["id", "name"], [typeof(int), typeof(string)], ["int", "nvarchar"], data, truncated, sizeBytes);
    }

    /// <summary>A reader of <paramref name="rows"/> rows with an int <c>id</c> (0-based) and a string <c>name</c>.</summary>
    public static DbDataReader Rows(int rows, Func<int, string?>? name = null)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("name", typeof(string));
        for (var i = 0; i < rows; i++)
        {
            table.Rows.Add(i, name?.Invoke(i) ?? $"name{i}");
        }

        return table.CreateDataReader();
    }

    public static ResultCacheLimits Limits(long total = 10_000, long entry = 10_000, long user = 10_000, int rows = 1000, int perUser = 100) =>
        new(total, entry, user, rows, perUser);

    public static ResultCacheEntry Entry(string hash, string sid, long size, DateTimeOffset now, TimeSpan? ttl = null, string tenant = "t1") =>
        new(hash, new TenantId(tenant), sid, [CacheHarness.Orders], Result(1, size),
            new ResultCacheEpochSnapshot(1, new Dictionary<TableIdentifier, long>(), 1, 0), now, now + (ttl ?? TimeSpan.FromMinutes(5)), "trace");
}
