namespace Autheris.Tests.Unit.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Application.State;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc;
using Autheris.Extensions.DataCatalog;
using Autheris.Extensions.OpenMetadata;
using Autheris.Infrastructure.Itsm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Autheris.Tests.Common;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class BackgroundServiceLockTests
{
    private sealed class DummyLease : IDistributedLease
    {
        private readonly CancellationTokenSource _lost = new();

        public bool Disposed { get; private set; }

        public string ResourceKey => "dummy";

        public long Token => 7;

        public CancellationToken LeaseLost => _lost.Token;

        public DateTimeOffset LastConfirmedAt => DateTimeOffset.UtcNow;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DummyLockLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IDistributedClusterStateProvider ClusterState(string keyFragment, bool acquired)
    {
        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.TryAcquireLeaseAsync(Arg.Is<string>(s => s.Contains(keyFragment)), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IDistributedLease?>(acquired ? new DummyLease() : null));
        return clusterState;
    }

    private static async Task RunAsync(BackgroundService service, FakeTimeProvider time, TimeSpan advance)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < (int)advance.TotalSeconds; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(3);
            }

            await Task.Delay(100);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    internal static IHostEnvironment DevelopmentEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return env;
    }

    private static IOptionsMonitor<GatewayOptions> Monitor(GatewayOptions options) => new StaticOptionsMonitor<GatewayOptions>(options);

    private static (OpenMetadataSyncBackgroundService Service, IOpenMetadataSyncService Sync) CreateOpenMetadata(
        IDistributedClusterStateProvider clusterState, FakeTimeProvider time)
    {
        var syncService = Substitute.For<IOpenMetadataSyncService>();
        syncService.SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OpenMetadataSyncResult(0, 0, 0, Array.Empty<TableIdentifier>(), Array.Empty<string>(), true)));
        var services = new ServiceCollection();
        services.AddSingleton(syncService);
        services.AddScoped<OpenMetadataSyncJob>();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var options = new GatewayOptions { OpenMetadata = new OpenMetadataOptions { Enabled = true, SyncIntervalMinutes = 1 } };
        var service = new OpenMetadataSyncBackgroundService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Monitor(options),
            clusterState,
            NullLogger<OpenMetadataSyncBackgroundService>.Instance,
            time);
        return (service, syncService);
    }

    [Fact]
    public async Task OpenMetadataSyncBackgroundService_WhenLockAcquired_ExecutesSync()
    {
        var time = new FakeTimeProvider();
        var (service, sync) = CreateOpenMetadata(ClusterState("openmetadata:sync", acquired: true), time);

        await RunAsync(service, time, TimeSpan.FromSeconds(10));

        await sync.Received().SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenMetadataSyncBackgroundService_WhenLockBusy_SkipsSync()
    {
        var time = new FakeTimeProvider();
        var (service, sync) = CreateOpenMetadata(ClusterState("openmetadata:sync", acquired: false), time);

        await RunAsync(service, time, TimeSpan.FromSeconds(10));

        await sync.DidNotReceive().SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>());
    }

    private static (DataCatalogSyncBackgroundService Service, IDataCatalogSyncService Sync) CreateDataCatalog(
        IDistributedClusterStateProvider clusterState, FakeTimeProvider time)
    {
        var syncService = Substitute.For<IDataCatalogSyncService>();
        syncService.SyncCatalogAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CatalogSyncResult(0, 0, 0, 0, Array.Empty<TableIdentifier>(), Array.Empty<string>(), true)));
        var services = new ServiceCollection();
        services.AddSingleton(syncService);
        services.AddScoped<DataCatalogSyncJob>();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var options = new GatewayOptions { Catalog = new DataCatalogOptions { Enabled = true, SyncIntervalMinutes = 1 } };
        var service = new DataCatalogSyncBackgroundService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Monitor(options),
            clusterState,
            NullLogger<DataCatalogSyncBackgroundService>.Instance,
            time);
        return (service, syncService);
    }

    [Fact]
    public async Task DataCatalogSyncBackgroundService_WhenLockAcquired_ExecutesSync()
    {
        var time = new FakeTimeProvider();
        var (service, sync) = CreateDataCatalog(ClusterState("datacatalog:sync", acquired: true), time);

        await RunAsync(service, time, TimeSpan.FromSeconds(30));

        await sync.Received().SyncCatalogAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DataCatalogSyncBackgroundService_WhenLockBusy_SkipsSync()
    {
        var time = new FakeTimeProvider();
        var (service, sync) = CreateDataCatalog(ClusterState("datacatalog:sync", acquired: false), time);

        await RunAsync(service, time, TimeSpan.FromSeconds(30));

        await sync.DidNotReceive().SyncCatalogAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsentRecertificationHostedService_WhenLockBusy_SkipsRecertification()
    {
        // Arrange
        var recertService = Substitute.For<IConsentRecertificationService>();

        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.TryAcquireLockAsync(Arg.Is<string>(s => s.Contains("recertification:scan")), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable?>((IAsyncDisposable?)null));

        var services = new ServiceCollection();
        services.AddSingleton(recertService);
        services.AddSingleton(clusterState);
        var sp = services.BuildServiceProvider();

        var service = new ConsentRecertificationHostedService(
            sp,
            NullLogger<ConsentRecertificationHostedService>.Instance,
            TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(80, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert
        await recertService.DidNotReceive().ScanAndTriggerExpiringConsentRecertificationsAsync(Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }
}
