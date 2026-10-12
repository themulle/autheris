namespace Autheris.Tests.Unit.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class BackgroundServiceLockTests
{
    private sealed class DummyLockLease : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task MssqlChangeTrackingHostedService_WhenLockAcquired_ExecutesPoller()
    {
        // Arrange
        var poller = Substitute.For<IMssqlChangeTrackingPoller>();
        poller.PollTableChangesAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1));

        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.TryAcquireLockAsync(Arg.Is<string>(s => s.Contains("cdc:mssql")), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable?>(new DummyLockLease()));

        var options = Options.Create(new GatewayOptions
        {
            MssqlChangeTracking = new MssqlChangeTrackingOptions
            {
                Enabled = true,
                PollingIntervalMilliseconds = 100,
                TrackedTables = new List<string> { "catalog.schema.orders" }
            }
        });

        var service = new MssqlChangeTrackingHostedService(
            poller,
            options,
            NullLogger<MssqlChangeTrackingHostedService>.Instance,
            clusterState);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert
        await poller.Received().PollTableChangesAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MssqlChangeTrackingHostedService_WhenLockBusy_SkipsPoller()
    {
        // Arrange
        var poller = Substitute.For<IMssqlChangeTrackingPoller>();

        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.TryAcquireLockAsync(Arg.Is<string>(s => s.Contains("cdc:mssql")), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable?>((IAsyncDisposable?)null));

        var options = Options.Create(new GatewayOptions
        {
            MssqlChangeTracking = new MssqlChangeTrackingOptions
            {
                Enabled = true,
                PollingIntervalMilliseconds = 100,
                TrackedTables = new List<string> { "catalog.schema.orders" }
            }
        });

        var service = new MssqlChangeTrackingHostedService(
            poller,
            options,
            NullLogger<MssqlChangeTrackingHostedService>.Instance,
            clusterState);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert
        await poller.DidNotReceive().PollTableChangesAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenMetadataSyncBackgroundService_WhenLockAcquired_ExecutesSync()
    {
        // Arrange
        var syncService = Substitute.For<IOpenMetadataSyncService>();
        syncService.SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OpenMetadataSyncResult(0, 0, 0, Array.Empty<TableIdentifier>(), Array.Empty<string>(), true)));

        var services = new ServiceCollection();
        services.AddSingleton(syncService);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.TryAcquireLockAsync(Arg.Is<string>(s => s.Contains("openmetadata:sync")), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable?>(new DummyLockLease()));

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                SyncIntervalMinutes = 1
            }
        });

        var service = new OpenMetadataSyncBackgroundService(
            scopeFactory,
            options,
            NullLogger<OpenMetadataSyncBackgroundService>.Instance,
            clusterState);

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
        await syncService.Received().SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenMetadataSyncBackgroundService_WhenLockBusy_SkipsSync()
    {
        // Arrange
        var syncService = Substitute.For<IOpenMetadataSyncService>();

        var services = new ServiceCollection();
        services.AddSingleton(syncService);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.TryAcquireLockAsync(Arg.Is<string>(s => s.Contains("openmetadata:sync")), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable?>((IAsyncDisposable?)null));

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                SyncIntervalMinutes = 1
            }
        });

        var service = new OpenMetadataSyncBackgroundService(
            scopeFactory,
            options,
            NullLogger<OpenMetadataSyncBackgroundService>.Instance,
            clusterState);

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
        await syncService.DidNotReceive().SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DataCatalogSyncBackgroundService_WhenLockBusy_SkipsSync()
    {
        // Arrange
        var syncService = Substitute.For<IDataCatalogSyncService>();

        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.TryAcquireLockAsync(Arg.Is<string>(s => s.Contains("datacatalog:sync")), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable?>((IAsyncDisposable?)null));

        var services = new ServiceCollection();
        services.AddSingleton(syncService);
        var sp = services.BuildServiceProvider();

        var options = Options.Create(new GatewayOptions
        {
            Catalog = new DataCatalogOptions
            {
                Enabled = true,
                SyncIntervalMinutes = 1
            }
        });

        var service = new DataCatalogSyncBackgroundService(
            sp,
            options,
            NullLogger<DataCatalogSyncBackgroundService>.Instance,
            clusterState);

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
        await syncService.DidNotReceive().SyncCatalogAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
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
