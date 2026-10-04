using System;
using System.Threading.Tasks;
using Garnet;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Autheris.Tests.Unit;

public class GarnetEmbeddedServerTests
{
    [Fact]
    public async Task GarnetServer_StartsAndRespondsToRedisClient()
    {
        int port = 3279;
        var server = new GarnetServer(new[] { "--port", port.ToString(), "--bind", "127.0.0.1" });
        server.Start();

        try
        {
            var config = new ConfigurationOptions
            {
                EndPoints = { $"127.0.0.1:{port}" },
                AbortOnConnectFail = false,
                ConnectTimeout = 2000,
                SyncTimeout = 1000
            };

            using var redis = await ConnectionMultiplexer.ConnectAsync(config);
            var db = redis.GetDatabase();

            var pingResult = await db.PingAsync();
            pingResult.ShouldBeGreaterThan(TimeSpan.Zero);

            bool setOk = await db.StringSetAsync("test:key", "garnet_rocks");
            setOk.ShouldBeTrue();

            var val = await db.StringGetAsync("test:key");
            val.ToString().ShouldBe("garnet_rocks");
        }
        finally
        {
            server.Dispose();
        }
    }
}
