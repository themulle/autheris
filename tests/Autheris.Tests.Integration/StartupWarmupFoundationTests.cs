namespace Autheris.Tests.Integration;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

/// <summary>
/// PLAN-OPS-150 WP-A: pins the host and HotChocolate behaviours the warm-up design relies on (V-1, PRD fact 13, A-150-2).
/// </summary>
public sealed class StartupWarmupFoundationTests
{
    private sealed class RecordingWarmupTask : IRequestExecutorWarmupTask
    {
        public ConcurrentQueue<IRequestExecutor> Executors { get; } = new();

        public bool ApplyOnlyOnStartup => false;

        public Task WarmupAsync(IRequestExecutor executor, CancellationToken cancellationToken)
        {
            Executors.Enqueue(executor);
            return Task.CompletedTask;
        }
    }

    private static WebApplicationFactory<Program> Create(RecordingWarmupTask? task = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-warmup-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            if (task is not null)
            {
                b.ConfigureServices(s => s.AddGraphQLServer().AddWarmupTask(task));
            }
        });

    [Fact]
    public void AddGraphQLServer_RegistersHotChocolateWarmupHostedService_Fact()
    {
        using var factory = Create();

        var hosted = factory.Services.GetServices<IHostedService>().Select(h => h.GetType().Name).ToList();

        hosted.ShouldContain("RequestExecutorWarmupService");
    }

    [Fact]
    public void HostStartup_UserHostedServicesStartBeforeKestrelListens()
    {
        using var factory = Create();

        var hosted = factory.Services.GetServices<IHostedService>().Select(h => h.GetType().Name).ToList();

        // GenericWebHostService starts the server (Kestrel listens); every hosted service registered before it completes
        // StartAsync first. A warm-up executed in StartAsync therefore delays the listener, a BackgroundService does not.
        var server = hosted.IndexOf("GenericWebHostService");
        server.ShouldBeGreaterThanOrEqualTo(0);
        hosted.IndexOf("RequestExecutorWarmupService").ShouldBeLessThan(server);
    }

    [Fact]
    public async Task CatalogSchemaRebuild_RunsWarmupTasksBeforeNewExecutorServes()
    {
        // A-150-2: do the warm-up tasks run for a rebuilt executor before GetExecutorAsync hands it out?
        var task = new RecordingWarmupTask();
        using var factory = Create(task);
        var provider = factory.Services.GetRequiredService<IRequestExecutorProvider>();
        var manager = factory.Services.GetRequiredService<IRequestExecutorManager>();

        var first = await provider.GetExecutorAsync(cancellationToken: CancellationToken.None);
        var initialRuns = task.Executors.Count;

        manager.EvictExecutor(first.Schema.Name);
        IRequestExecutor second = first;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (ReferenceEquals(second, first) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, CancellationToken.None);
            second = await provider.GetExecutorAsync(cancellationToken: CancellationToken.None);
        }

        ReferenceEquals(second, first).ShouldBeFalse("eviction must produce a new executor");
        task.Executors.Count.ShouldBeGreaterThan(initialRuns, "warm-up tasks run again for a rebuilt executor");

        // Finding: the task already ran for the new executor when GetExecutorAsync returned it.
        task.Executors.ShouldContain(e => ReferenceEquals(e, second));
    }

    private static WebApplicationFactory<Program> CreateWithColdPath(bool enabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-coldpath-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            b.UseSetting("Gateway:Startup:ColdPathTiming:Enabled", enabled ? "true" : "false");
        });

    [Fact]
    public void ColdPathTiming_HostRegistersListenerOnlyWhenEnabled()
    {
        using var enabled = CreateWithColdPath(true);
        using var disabled = CreateWithColdPath(false);

        enabled.Services.GetService<Autheris.Api.Hosting.Warmup.ColdPathTimingListener>().ShouldNotBeNull();
        disabled.Services.GetService<Autheris.Api.Hosting.Warmup.ColdPathTimingListener>().ShouldBeNull();
    }
}
