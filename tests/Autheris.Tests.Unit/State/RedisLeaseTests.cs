namespace Autheris.Tests.Unit.State;

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Domain.Options;
using Autheris.Infrastructure.State;
using Garnet;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using StackExchange.Redis;
using Xunit;

/// <summary>Lease contract against a real RESP server: embedded Garnet with Lua scripting enabled.</summary>
public sealed class RedisLeaseTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private GarnetServer? _server;
    private ConnectionMultiplexer? _mux;
    private RedisClusterStateProvider _provider = null!;

    public async Task InitializeAsync()
    {
        var port = GetFreePort();
        _server = new GarnetServer(["--port", port.ToString(), "--bind", "127.0.0.1", "--lua"]);
        _server.Start();
        _mux = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { $"127.0.0.1:{port}" },
            AbortOnConnectFail = false,
            ConnectTimeout = 2000,
            SyncTimeout = 2000,
            AsyncTimeout = 2000,
        });
        _provider = CreateProvider(_mux);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _mux?.Dispose();
        _server?.Dispose();
    }

    private static RedisClusterStateProvider CreateProvider(IConnectionMultiplexer mux) =>
        new(mux, Options.Create(new GatewayOptions()), NullLogger<RedisClusterStateProvider>.Instance);

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Lease_IsRenewed_WhileJobRuns_LongerThanInitialTtl()
    {
        await using var lease = await _provider.TryAcquireLeaseAsync("job", TimeSpan.FromSeconds(3));
        lease.ShouldNotBeNull();

        await Task.Delay(7000);

        lease.LeaseLost.IsCancellationRequested.ShouldBeFalse();
        (await _provider.TryAcquireLeaseAsync("job", Ttl)).ShouldBeNull();
    }

    [Fact]
    public async Task SecondAcquire_WhileHeld_ReturnsNull()
    {
        await using var first = await _provider.TryAcquireLeaseAsync("job", Ttl);
        var second = await _provider.TryAcquireLeaseAsync("job", Ttl);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task Token_IsStrictlyIncreasing_AcrossAcquisitions()
    {
        var first = await _provider.TryAcquireLeaseAsync("job", Ttl);
        var firstToken = first!.Token;
        await first.DisposeAsync();
        var second = await _provider.TryAcquireLeaseAsync("job", Ttl);
        await using var _ = second;

        second!.Token.ShouldBeGreaterThan(firstToken);
    }

    [Fact]
    public async Task Release_ByStaleOwner_DoesNotDeleteNewOwnersLease()
    {
        var stale = await _provider.TryAcquireLeaseAsync("job", TimeSpan.FromMilliseconds(300));
        stale.ShouldNotBeNull();
        // Simulate expiry plus takeover through direct key manipulation of the same resource.
        var db = _mux!.GetDatabase();
        var keys = _mux.GetServer(_mux.GetEndPoints()[0]).Keys(pattern: "*lease:{job}");
        foreach (var key in keys)
        {
            await db.KeyDeleteAsync(key);
        }

        await using var current = await _provider.TryAcquireLeaseAsync("job", Ttl);
        current.ShouldNotBeNull();

        await stale.DisposeAsync();

        (await _provider.TryAcquireLeaseAsync("job", Ttl)).ShouldBeNull();
    }

    [Fact]
    public async Task Token_AfterFenceCounterLoss_IsStillAboveThePreviousToken()
    {
        var first = await _provider.TryAcquireLeaseAsync("job", Ttl);
        var oldToken = first!.Token;
        await first.DisposeAsync();
        var db = _mux!.GetDatabase();
        foreach (var key in _mux.GetServer(_mux.GetEndPoints()[0]).Keys(pattern: "*lease:{job}*"))
        {
            await db.KeyDeleteAsync(key); // the store restarted without persistence
        }

        await Task.Delay(20);
        await using var second = await _provider.TryAcquireLeaseAsync("job", Ttl);

        second!.Token.ShouldBeGreaterThan(oldToken);
    }

    [Theory]
    [InlineData("a{b")]
    [InlineData("a}b")]
    public async Task Acquire_WithBracesInKey_IsRejected(string key)
    {
        await Should.ThrowAsync<ArgumentException>(async () => await _provider.TryAcquireLeaseAsync(key, Ttl));
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

    [Fact]
    public void EmbeddedGarnetArguments_EnableLuaScripting()
    {
        var args = Autheris.Infrastructure.Garnet.GarnetServerManager.BuildServerArguments(new GarnetOptions(), "pw", null);

        args.ShouldContain("--lua");
    }
}
