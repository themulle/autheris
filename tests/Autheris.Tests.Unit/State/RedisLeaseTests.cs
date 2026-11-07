namespace Autheris.Tests.Unit.State;

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Domain.Options;
using Autheris.Infrastructure.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using StackExchange.Redis;
using Xunit;

/// <summary>Lease contract against Redis.</summary>
public sealed class RedisLeaseTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private static RedisClusterStateProvider CreateProvider(IConnectionMultiplexer mux) =>
        new(mux, Options.Create(new GatewayOptions()), NullLogger<RedisClusterStateProvider>.Instance);

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Theory]
    [InlineData("a{b")]
    [InlineData("a}b")]
    public async Task Acquire_WithBracesInKey_IsRejected(string key)
    {
        var port = GetFreePort();
        using var mux = ConnectionMultiplexer.Connect(new ConfigurationOptions
        {
            EndPoints = { $"127.0.0.1:{port}" },
            AbortOnConnectFail = false,
            ConnectTimeout = 200,
            SyncTimeout = 200
        });
        var provider = CreateProvider(mux);
        await Should.ThrowAsync<ArgumentException>(async () => await provider.TryAcquireLeaseAsync(key, Ttl));
    }

    [Fact]
    public async Task Acquire_WhenRedisDown_ThrowsClusterStateUnavailable()
    {
        var port = GetFreePort();
        using var mux = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { $"127.0.0.1:{port}" },
            AbortOnConnectFail = false,
            ConnectTimeout = 300,
            SyncTimeout = 300,
            AsyncTimeout = 300,
        });
        var provider = CreateProvider(mux);

        var act = async () => await provider.TryAcquireLeaseAsync("job", Ttl);

        await Should.ThrowAsync<ClusterStateUnavailableException>(act);
    }
}
