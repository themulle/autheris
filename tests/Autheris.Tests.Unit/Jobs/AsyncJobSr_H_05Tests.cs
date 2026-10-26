using Autheris.Tests.Common;
namespace Autheris.Tests.Unit.Jobs;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Jobs.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SR-H-05: client MaxRows / Timeout of an async job never exceed the server-side bounds.</summary>
public sealed class AsyncJobSr_H_05Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-sr-h-05-" + Guid.NewGuid().ToString("N"));
    private readonly IHostEnvironment _env = Substitute.For<IHostEnvironment>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();

    public AsyncJobSr_H_05Tests()
    {
        Directory.CreateDirectory(_dir);
        _env.ContentRootPath.Returns(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static IOptions<GatewayOptions> Opts(long? asyncMax = 500, int maxTimeout = 60, int defTimeout = 20, int purgeMinutes = 15) =>
        Options.Create(new GatewayOptions
        {
            RowLimits = new TransportRowLimitsOptions { AsyncJob = new ChannelRowLimitOptions { MaxAllowedRows = asyncMax } },
            AsyncJobs = new AsyncJobOptions { MaxTimeoutSeconds = maxTimeout, DefaultTimeoutSeconds = defTimeout, PurgeIntervalMinutes = purgeMinutes }
        });

    private static ClaimsPrincipal User() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim("sub", "alice"), new Claim("tenant_id", "t")], "Test"));

    private async Task<GovernedSqlQueryRequest> RunWorkerAsync(AsyncQueryJobRequest request, IOptions<GatewayOptions> options)
    {
        var manager = new AsyncQueryJobManager(_audit, environment: _env, options: options);
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        GovernedSqlQueryRequest? captured = null;
        sql.ExecuteQueryBufferedAsync(Arg.Do<GovernedSqlQueryRequest>(r => captured = r), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GovernedSqlResult("s", ["id"], new List<IReadOnlyDictionary<string, object?>>(), 0, 1)));
        var services = new ServiceCollection();
        services.AddScoped(_ => sql);
        var worker = new AsyncQueryJobBackgroundWorker(manager, manager, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _audit, NullLogger<AsyncQueryJobBackgroundWorker>.Instance, options);
        var d = await manager.SubmitJobAsync(request, User(), new TenantId("t"));
        await worker.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => captured is not null, TimeSpan.FromSeconds(10));
        await WaitHelper.WaitUntil(async () => (await manager.GetJobAsync(d.JobId, new TenantId("t"), "alice"))?.State == AsyncJobState.Completed, TimeSpan.FromSeconds(10), pollInterval: TimeSpan.FromMilliseconds(50));
        await worker.StopAsync(CancellationToken.None);
        captured.ShouldNotBeNull();
        return captured;
    }

    [Theory]
    [InlineData(int.MaxValue, 500)]
    [InlineData(100_000, 500)]
    [InlineData(10, 10)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task SR_H_05_Worker_ClampsClientMaxRowsToConfiguredCap(int clientRows, long expected)
    {
        var q = await RunWorkerAsync(new AsyncQueryJobRequest("SELECT 1", MaxRows: clientRows), Opts());

        q.RowLimit.ShouldNotBeNull();
        q.RowLimit!.DefaultMaxRows.ShouldBe(expected);
        q.RowLimit.MaxAllowedRows.ShouldBe(expected);
    }

    [Fact]
    public async Task SR_H_05_Worker_WithoutAsyncChannelOption_FallsBackToWebSqlCap()
    {
        var options = Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { MaxAllowedRows = 77 } });

        var q = await RunWorkerAsync(new AsyncQueryJobRequest("SELECT 1", MaxRows: int.MaxValue), options);

        q.RowLimit!.MaxAllowedRows.ShouldBe(77);
    }

    [Fact]
    public async Task SR_H_05_Worker_RederivesBoundsEvenForUnnormalizedQueuedItem()
    {
        var options = Opts();
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        GovernedSqlQueryRequest? captured = null;
        sql.ExecuteQueryBufferedAsync(Arg.Do<GovernedSqlQueryRequest>(r => captured = r), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GovernedSqlResult("s", ["id"], new List<IReadOnlyDictionary<string, object?>>(), 0, 1)));
        var manager = new AsyncQueryJobManager(_audit, environment: _env, options: options);
        var services = new ServiceCollection();
        services.AddScoped(_ => sql);
        var worker = new AsyncQueryJobBackgroundWorker(manager, manager, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _audit, NullLogger<AsyncQueryJobBackgroundWorker>.Instance, options);

        var request = new AsyncQueryJobRequest("SELECT 1", MaxRows: int.MaxValue, Timeout: TimeSpan.FromDays(30));
        var descriptor = new AsyncJobDescriptor("j1", "t", "alice", "SELECT 1", "json", AsyncJobState.Queued, DateTimeOffset.UtcNow);
        using var jobCts = new CancellationTokenSource();
        var ch = typeof(AsyncQueryJobManager).GetField("_channel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var channel = (System.Threading.Channels.Channel<AsyncJobExecutionItem>)ch.GetValue(manager)!;
        await channel.Writer.WriteAsync(new AsyncJobExecutionItem(descriptor, request, User(), jobCts));

        await worker.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => captured is not null, TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.RowLimit!.MaxAllowedRows.ShouldBe(500);
    }

    [Fact]
    public async Task SR_H_05_Submit_ClampsHugeTimeoutToConfiguredMaximum()
    {
        var manager = new AsyncQueryJobManager(_audit, environment: _env, options: Opts(maxTimeout: 60));

        await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1", MaxRows: int.MaxValue, Timeout: TimeSpan.FromDays(365)), User(), new TenantId("t"));

        manager.Reader.TryRead(out var item).ShouldBeTrue();
        item!.Request.Timeout.ShouldBe(TimeSpan.FromSeconds(60));
        item.Request.MaxRows.ShouldBe(500);
    }

    [Theory]
    [MemberData(nameof(MissingTimeouts))]
    public async Task SR_H_05_Submit_AppliesDefaultTimeoutWhenAbsentOrNonPositive(TimeSpan? timeout)
    {
        var manager = new AsyncQueryJobManager(_audit, environment: _env, options: Opts(defTimeout: 20));

        await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1", Timeout: timeout), User(), new TenantId("t"));

        manager.Reader.TryRead(out var item).ShouldBeTrue();
        item!.Request.Timeout.ShouldBe(TimeSpan.FromSeconds(20));
    }

    public static IEnumerable<object?[]> MissingTimeouts() =>
    [
        [null],
        [TimeSpan.Zero],
        [TimeSpan.FromSeconds(-3)],
    ];

    [Fact]
    public async Task SR_H_05_Submit_KeepsSmallerClientTimeout()
    {
        var manager = new AsyncQueryJobManager(_audit, environment: _env, options: Opts(maxTimeout: 60));

        await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1", Timeout: TimeSpan.FromSeconds(5)), User(), new TenantId("t"));

        manager.Reader.TryRead(out var item).ShouldBeTrue();
        item!.Request.Timeout.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SR_M_21_Worker_PurgesExpiredJobsPeriodically()
    {
        var manager = new AsyncQueryJobManager(_audit, environment: _env, options: Opts(purgeMinutes: 1));
        var options = Options.Create(new GatewayOptions { AsyncJobs = new AsyncJobOptions { PurgeIntervalMinutes = 1, RetentionHours = 0 } });
        var worker = new AsyncQueryJobBackgroundWorker(manager, manager, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _audit, NullLogger<AsyncQueryJobBackgroundWorker>.Instance, options);
        var d = new AsyncJobDescriptor("old", "t", "alice", "q", "json", AsyncJobState.Completed, DateTimeOffset.UtcNow.AddHours(-2), CompletedAt: DateTimeOffset.UtcNow.AddHours(-2));
        manager.UpdateJob(d);

        await worker.PurgeOnceAsync(CancellationToken.None);

        (await manager.GetJobAsync("old", new TenantId("t"), "alice")).ShouldBeNull();
    }
}
