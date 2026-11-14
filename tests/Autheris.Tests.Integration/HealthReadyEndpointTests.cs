namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Hosting.Warmup;
using Autheris.Application.Common.Interfaces;
using Autheris.Infrastructure.Health;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class HealthReadyEndpointTests
{
    private sealed class CountingHealthCheckService(IGatewayHealthCheckService inner) : IGatewayHealthCheckService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Reset() => Interlocked.Exchange(ref _calls, 0);

        public Task<GatewayHealthReport> CheckHealthAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.CheckHealthAsync(ct);
        }
    }

    [Fact]
    public async Task Anonymous_burst_causes_bounded_dependency_pings()
    {
        CountingHealthCheckService? counter = null;
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("HealthReadyBurst"));
            builder.UseSetting("Gateway:Startup:Warmup:Enabled", "false");
            builder.UseSyntheticSqlData();
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGatewayHealthCheckService>();
                services.AddSingleton<IGatewayHealthCheckService>(sp =>
                {
                    counter = new CountingHealthCheckService(sp.GetRequiredService<GatewayHealthCheckService>());
                    return counter;
                });
            });
        });

        using var client = factory.CreateClient();

        var hosted = factory.Services.GetServices<IHostedService>().OfType<BackgroundService>()
            .Where(h => h is GraphQlExecutorGate);
        foreach (var service in hosted)
        {
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(60));
        }

        // Resolve ReadinessProbe so the counting wrapper is wired, then reset before the burst.
        _ = factory.Services.GetRequiredService<Autheris.Api.Endpoints.Health.ReadinessProbe>();
        counter!.Reset();

        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => client.GetAsync("/health/ready")));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        counter.Calls.ShouldBeInRange(1, 2);
    }
}
