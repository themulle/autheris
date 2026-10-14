namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

public sealed class MssqlChangeTrackingHostedServiceTests
{
    private static MssqlChangeTrackingHostedService Create(IMssqlWatermarkStore store, string environment, bool enabled = true)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        var options = new GatewayOptions
        {
            MssqlChangeTracking = new MssqlChangeTrackingOptions
            {
                Enabled = enabled,
                TrackedTables = new List<string> { "catalog.schema.orders" }
            }
        };
        return new MssqlChangeTrackingHostedService(
            Substitute.For<IServiceScopeFactory>(),
            new StaticOptionsMonitor<GatewayOptions>(options),
            Substitute.For<IDistributedClusterStateProvider>(),
            store,
            env,
            NullLogger<MssqlChangeTrackingHostedService>.Instance);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NonDurableStore_OutsideDevelopment_FailsStart(string environment)
    {
        var service = Create(new InMemoryMssqlWatermarkStore(), environment);

        await Should.ThrowAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NonDurableStore_InDevelopment_Starts()
    {
        var service = Create(new InMemoryMssqlWatermarkStore(), "Development");

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DurableStore_InProduction_Starts()
    {
        var store = Substitute.For<IMssqlWatermarkStore>();
        store.IsDurable.Returns(true);
        var service = Create(store, "Production");

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }
}
