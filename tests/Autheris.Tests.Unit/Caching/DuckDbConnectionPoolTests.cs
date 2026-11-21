using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Infrastructure.Caching.TableCache.Storage;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Caching;

public sealed class DuckDbConnectionPoolTests
{
    [Fact]
    public async Task LeaseReaderAsync_EnforcesMaxConcurrentReaders_UntilReturned()
    {
        using var pool = new DuckDbConnectionPool(
            "DataSource=:memory:",
            maxConcurrentReaders: 2,
            maxIdleReaders: 2);

        using (await pool.AcquireWriterLockAsync())
        {
            pool.AvailableReaderSlots.ShouldBe(2);
        }

        var r1 = await pool.LeaseReaderAsync();
        var r2 = await pool.LeaseReaderAsync();
        pool.LeasedCount.ShouldBe(2);
        pool.AvailableReaderSlots.ShouldBe(0);

        using var shortCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Should.ThrowAsync<OperationCanceledException>(() => pool.LeaseReaderAsync(shortCts.Token));

        var waitingTask = pool.LeaseReaderAsync();
        waitingTask.IsCompleted.ShouldBeFalse();

        pool.ReturnReader(r1);
        var r3 = await waitingTask.WaitAsync(TimeSpan.FromSeconds(5));
        pool.LeasedCount.ShouldBe(2);

        pool.ReturnReader(r2);
        pool.ReturnReader(r3);
        pool.LeasedCount.ShouldBe(0);
        pool.AvailableReaderSlots.ShouldBe(2);
    }

    [Fact]
    public async Task ReturnReader_CapsIdleReadersAndTrimIdleExpiresOldConnections()
    {
        using var pool = new DuckDbConnectionPool(
            "DataSource=:memory:",
            maxConcurrentReaders: 4,
            maxIdleReaders: 2,
            idleTimeout: TimeSpan.FromMilliseconds(80));

        var leased = new List<DbConnection>(4);
        for (int i = 0; i < 4; i++)
        {
            leased.Add(await pool.LeaseReaderAsync());
        }

        foreach (var conn in leased)
        {
            pool.ReturnReader(conn);
        }

        pool.IdleCount.ShouldBe(2);

        await Task.Delay(130);
        pool.TrimIdle();
        pool.IdleCount.ShouldBe(0);
    }
}
