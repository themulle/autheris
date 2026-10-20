using System.Net;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Api.Hosting.Warmup;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

/// <summary>PLAN-OPS-150 WP-B: readiness order drain, health report, executor gate, best-effort warm-up (D-150-2, SEC-OPS150-04/-09).</summary>
public sealed class StartupWarmupReadinessTests
{
    private sealed class FakeGate(ExecutorGateState state) : IGraphQlExecutorGate
    {
        public ExecutorGateState State { get; set; } = state;
    }

    private sealed record Setup(
        string Environment = "Production",
        ExecutorGateState? Gate = ExecutorGateState.Ready,
        WarmupPhase? Warmup = WarmupPhase.Ready,
        bool Draining = false,
        bool Healthy = true);

    private sealed class Probe : IAsyncDisposable
    {
        public required WebApplication App { get; init; }

        public required HttpClient Client { get; init; }

        public required FakeGate? Gate { get; init; }

        public required StartupWarmupState? Warmup { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private static async Task<Probe> StartAsync(Setup setup)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = setup.Environment });
        builder.WebHost.UseTestServer();

        var drain = Substitute.For<ITrafficDrainController>();
        drain.IsDraining.Returns(setup.Draining);
        builder.Services.AddSingleton(drain);

        var health = Substitute.For<IGatewayHealthCheckService>();
        health.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns(new GatewayHealthReport(setup.Healthy, []));
        builder.Services.AddSingleton(health);

        FakeGate? gate = null;
        if (setup.Gate is { } g)
        {
            gate = new FakeGate(g);
            builder.Services.AddSingleton<IGraphQlExecutorGate>(gate);
        }

        StartupWarmupState? warmup = null;
        if (setup.Warmup is { } w)
        {
            warmup = new StartupWarmupState(w);
            builder.Services.AddSingleton<IStartupWarmupState>(warmup);
        }

        var app = builder.Build();
        app.MapHealthEndpoints(new GatewayOptions(), app.Environment);
        await app.StartAsync();
        return new Probe { App = app, Client = app.GetTestClient(), Gate = gate, Warmup = warmup };
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadyAsync(Probe probe)
    {
        var response = await probe.Client.GetAsync("/health/ready");
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string[] Props(JsonElement body) => body.EnumerateObject().Select(p => p.Name).Order().ToArray();

    [Fact]
    public async Task Ready_WhileWarming_Returns503Warming_ProductionBodyHasStatusAndTimestampOnly()
    {
        await using var probe = await StartAsync(new Setup(Warmup: WarmupPhase.Warming));

        var (status, body) = await ReadyAsync(probe);

        status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        body.GetProperty("status").GetString().ShouldBe("Warming");
        Props(body).ShouldBe(["status", "timestamp"]);
    }

    [Fact]
    public async Task Ready_AfterWarmup_Returns200()
    {
        await using var probe = await StartAsync(new Setup());

        var (status, body) = await ReadyAsync(probe);

        status.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("status").GetString().ShouldBe("Ready");
    }

    [Fact]
    public async Task Ready_WarmupDone_NotDelayedByHealthCache()
    {
        await using var probe = await StartAsync(new Setup(Warmup: WarmupPhase.Warming));
        (await ReadyAsync(probe)).Status.ShouldBe(HttpStatusCode.ServiceUnavailable);

        probe.Warmup!.Phase.ShouldBe(WarmupPhase.Warming);
        SetPhase(probe.Warmup, WarmupPhase.Ready);

        // The healthy component report is the cached part; the very next request reflects the live warm-up state.
        (await ReadyAsync(probe)).Status.ShouldBe(HttpStatusCode.OK);
    }

    private static void SetPhase(StartupWarmupState state, WarmupPhase phase) =>
        typeof(StartupWarmupState).GetMethod("Set", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(state, [phase]);

    [Fact]
    public async Task Ready_UnhealthyComponent_WinsOverWarming()
    {
        await using var probe = await StartAsync(new Setup(Gate: ExecutorGateState.Pending, Warmup: WarmupPhase.Warming, Healthy: false));

        var (status, body) = await ReadyAsync(probe);

        status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        body.GetProperty("status").GetString().ShouldBe("Unhealthy");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Ready_UnhealthySecurityComponent_WinsOverWarmingAndReady()
    {
        // T-OPS150-05 / SEC-OPS150-04
        foreach (var warmup in new[] { WarmupPhase.Pending, WarmupPhase.Warming, WarmupPhase.Ready })
        {
            await using var probe = await StartAsync(new Setup(Warmup: warmup, Healthy: false));

            var (status, body) = await ReadyAsync(probe);

            status.ShouldBe(HttpStatusCode.ServiceUnavailable);
            body.GetProperty("status").GetString().ShouldBe("Unhealthy");
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Ready_DrainWinsOverEveryWarmupState()
    {
        // T-OPS150-05: drain answers 503 without a body regardless of gate, warm-up and health.
        foreach (var gate in Enum.GetValues<ExecutorGateState>())
        {
            foreach (var warmup in Enum.GetValues<WarmupPhase>())
            {
                await using var probe = await StartAsync(new Setup(Gate: gate, Warmup: warmup, Draining: true));

                var response = await probe.Client.GetAsync("/health/ready");

                response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
                (await response.Content.ReadAsStringAsync()).ShouldBeEmpty();
            }
        }
    }

    [Fact]
    public async Task Ready_Draining_Returns503RegardlessOfWarmup()
    {
        await using var probe = await StartAsync(new Setup(Draining: true));

        (await ReadyAsync(probe)).Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Live_IgnoresWarmupState()
    {
        await using var probe = await StartAsync(new Setup(Gate: ExecutorGateState.Failed, Warmup: WarmupPhase.Pending, Healthy: false));

        var response = await probe.Client.GetAsync("/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(null, null)]
    [InlineData(ExecutorGateState.Ready, null)]
    [InlineData(ExecutorGateState.Ready, WarmupPhase.Pending)]
    [InlineData(null, WarmupPhase.Ready)]
    [InlineData(ExecutorGateState.Pending, WarmupPhase.Ready)]
    [InlineData(ExecutorGateState.Failed, WarmupPhase.Ready)]
    public async Task Ready_UninitialisedWarmupState_Returns503(ExecutorGateState? gate, WarmupPhase? warmup)
    {
        // T-OPS150-06 / SEC-OPS150-01: missing registration, Pending and Failed all fail closed.
        await using var probe = await StartAsync(new Setup(Gate: gate, Warmup: warmup));

        (await ReadyAsync(probe)).Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(ExecutorGateState.Pending, WarmupPhase.Ready, "Warming")]
    [InlineData(ExecutorGateState.Failed, WarmupPhase.Ready, "Unhealthy")]
    [InlineData(ExecutorGateState.Ready, WarmupPhase.Warming, "Warming")]
    [InlineData(ExecutorGateState.Ready, WarmupPhase.Pending, "Warming")]
    public async Task ReadyBody_Production_WarmingAndUnhealthy_OnlyStatusAndTimestamp(ExecutorGateState gate, WarmupPhase warmup, string expected)
    {
        // T-OPS150-12 / SEC-OPS150-09
        await using var probe = await StartAsync(new Setup(Gate: gate, Warmup: warmup));

        var (status, body) = await ReadyAsync(probe);

        status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        body.GetProperty("status").GetString().ShouldBe(expected);
        Props(body).ShouldBe(["status", "timestamp"]);
    }

    [Fact]
    public async Task ReadyBody_Development_WarmingCarriesDetails()
    {
        await using var probe = await StartAsync(new Setup(Environment: "Development", Gate: ExecutorGateState.Failed, Warmup: WarmupPhase.Warming));

        var (_, body) = await ReadyAsync(probe);

        body.GetProperty("executorGate").GetString().ShouldBe("Failed");
        body.GetProperty("warmupPhase").GetString().ShouldBe("Warming");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Ready_GateRecoversAfterFailure_Returns200()
    {
        await using var probe = await StartAsync(new Setup(Gate: ExecutorGateState.Failed));
        (await ReadyAsync(probe)).Status.ShouldBe(HttpStatusCode.ServiceUnavailable);

        probe.Gate!.State = ExecutorGateState.Ready;

        (await ReadyAsync(probe)).Status.ShouldBe(HttpStatusCode.OK);
    }

    // ---- full host ----

    private static WebApplicationFactory<Program> Factory(bool? warmupEnabled)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-warmup-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            if (warmupEnabled is { } enabled)
            {
                builder.UseSetting("Gateway:Startup:Warmup:Enabled", enabled ? "true" : "false");
            }

            builder.UseSyntheticSqlData();
        });
        return factory;
    }

    /// <summary>Awaits the gate (and the best-effort service when registered) instead of polling the endpoint.</summary>
    private static async Task WaitForStartupAsync(WebApplicationFactory<Program> factory)
    {
        var hosted = factory.Services.GetServices<IHostedService>().OfType<BackgroundService>()
            .Where(h => h is GraphQlExecutorGate or StartupWarmupService);
        foreach (var service in hosted)
        {
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public async Task FullHost_ReadyReached_WithRealExecutorGate()
    {
        await using var factory = Factory(null);
        using var client = factory.CreateClient();

        await WaitForStartupAsync(factory);

        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Services.GetRequiredService<IGraphQlExecutorGate>().State.ShouldBe(ExecutorGateState.Ready);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task WarmupDisabled_ReadyImmediately_NoHostedService()
    {
        // T-OPS150-01: disabled removes the best-effort service only; the gate is still registered and still decides readiness.
        await using var factory = Factory(false);
        using var client = factory.CreateClient();

        var hosted = factory.Services.GetServices<IHostedService>().ToList();
        hosted.ShouldNotContain(h => h is StartupWarmupService);
        hosted.ShouldContain(h => h is GraphQlExecutorGate);
        factory.Services.GetRequiredService<IStartupWarmupState>().Phase.ShouldBe(WarmupPhase.Ready);

        await WaitForStartupAsync(factory);

        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RouteTable_Unchanged_NoWarmupEndpoint()
    {
        await using var factory = Factory(null);
        using var client = factory.CreateClient();

        var patterns = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();

        patterns.ShouldContain("/health/live");
        patterns.ShouldContain("/health/ready");
        patterns.ShouldNotContain(p => p.Contains("warm", StringComparison.OrdinalIgnoreCase));
        patterns.Count(p => p.StartsWith("/health", StringComparison.Ordinal)).ShouldBe(2);
    }
}
