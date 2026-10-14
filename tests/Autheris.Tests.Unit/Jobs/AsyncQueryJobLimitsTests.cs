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
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SR-H-05: the client-supplied MaxRows / Timeout of an async job may only lower the configured caps.</summary>
public sealed class AsyncQueryJobLimitsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-job-limits-" + Guid.NewGuid().ToString("N"));
    private readonly IHostEnvironment _environment = Substitute.For<IHostEnvironment>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();

    public AsyncQueryJobLimitsTests()
    {
        Directory.CreateDirectory(_dir);
        _environment.ContentRootPath.Returns(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim("sub", "alice") }, "TestAuth"));

    private static IOptions<GatewayOptions> Options(
        long webSqlMaxAllowed = 500,
        long hardCap = 100_000,
        int maxTimeoutSeconds = 600,
        long? channelMaxAllowed = null) =>
        Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { DefaultMaxRows = 100, MaxAllowedRows = webSqlMaxAllowed },
            RowLimits = new TransportRowLimitsOptions { AsyncJob = new ChannelRowLimitOptions { MaxAllowedRows = channelMaxAllowed } },
            AsyncJobs = new AsyncJobOptions { MaxRowsHardCap = hardCap, MaxTimeoutSeconds = maxTimeoutSeconds }
        });

    private async Task<GovernedSqlQueryRequest> RunWorkerAsync(IOptions<GatewayOptions> options, AsyncQueryJobRequest request)
    {
        var manager = new AsyncQueryJobManager(_audit, environment: _environment, options: options);
        GovernedSqlQueryRequest? captured = null;
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<GovernedSqlQueryRequest>();
                return Task.FromResult(new GovernedSqlResult("q", "q", new[] { "id" }, new List<IReadOnlyDictionary<string, object?>>(), 0, 1));
            });

        var services = new ServiceCollection();
        services.AddScoped(_ => sql);
        var worker = new AsyncQueryJobBackgroundWorker(
            manager, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _audit, options: options);

        await manager.SubmitJobAsync(request, User(), new TenantId("t"));
        await worker.StartAsync(CancellationToken.None);
        for (var i = 0; i < 100 && captured is null; i++)
        {
            await Task.Delay(50);
        }

        await worker.StopAsync(CancellationToken.None);
        captured.ShouldNotBeNull();
        return captured!;
    }

    [Fact]
    public async Task SR_H_05_MaxRows_AboveWebSqlLimit_IsClampedToWebSqlLimit()
    {
        var request = await RunWorkerAsync(Options(webSqlMaxAllowed: 500), new AsyncQueryJobRequest("SELECT 1", MaxRows: int.MaxValue));

        request.RowLimit!.MaxAllowedRows.ShouldBe(500);
        request.RowLimit.DefaultMaxRows.ShouldBe(500);
    }

    [Fact]
    public async Task SR_H_05_MaxRows_AboveHardCap_IsClampedToHardCap()
    {
        var request = await RunWorkerAsync(Options(webSqlMaxAllowed: 0, hardCap: 2_000), new AsyncQueryJobRequest("SELECT 1", MaxRows: int.MaxValue));

        request.RowLimit!.MaxAllowedRows.ShouldBe(2_000);
        request.RowLimit.DefaultMaxRows.ShouldBe(2_000);
    }

    [Fact]
    public async Task SR_H_05_MaxRows_AboveAsyncJobChannelLimit_IsClampedToChannelLimit()
    {
        var request = await RunWorkerAsync(Options(webSqlMaxAllowed: 500, channelMaxAllowed: 50), new AsyncQueryJobRequest("SELECT 1", MaxRows: 400));

        request.RowLimit!.MaxAllowedRows.ShouldBe(50);
    }

    [Fact]
    public async Task SR_H_05_MaxRows_BelowCaps_IsHonoured()
    {
        var request = await RunWorkerAsync(Options(webSqlMaxAllowed: 500), new AsyncQueryJobRequest("SELECT 1", MaxRows: 20));

        request.RowLimit!.MaxAllowedRows.ShouldBe(20);
        request.RowLimit.DefaultMaxRows.ShouldBe(20);
    }

    [Fact]
    public async Task SR_H_05_MaxRows_NonPositive_FailsClosedToOneRow()
    {
        var request = await RunWorkerAsync(Options(), new AsyncQueryJobRequest("SELECT 1", MaxRows: -5));

        request.RowLimit!.MaxAllowedRows.ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(86_400.0)]
    public async Task SR_H_05_Timeout_IsArmedAndClampedToConfiguredMaximum(double? requestedSeconds)
    {
        var options = Options(maxTimeoutSeconds: 1);
        var manager = new AsyncQueryJobManager(_audit, environment: _environment, options: options);
        TimeSpan? timeout = requestedSeconds is null ? null : TimeSpan.FromSeconds(requestedSeconds.Value);

        await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1", Timeout: timeout), User(), new TenantId("t"));

        manager.ChannelReader.TryRead(out var item).ShouldBeTrue();
        for (var i = 0; i < 60 && !item!.Cts.IsCancellationRequested; i++)
        {
            await Task.Delay(50);
        }

        item!.Cts.IsCancellationRequested.ShouldBeTrue();
    }
}
