namespace Autheris.Tests.Unit.Identity;

using System;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using NSubstitute;
using Xunit;

[Trait("Category", "Security")]
public class BasicAuthLockoutAtomicityTests
{
    [Fact]
    public async Task SEC_12H_05_BasicAuthAttemptGuard_UsesAtomicLuaScript_WhenRedisConnected()
    {
        var redis = Substitute.For<StackExchange.Redis.IConnectionMultiplexer>();
        var db = Substitute.For<StackExchange.Redis.IDatabase>();
        redis.IsConnected.Returns(true);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var options = new BasicAuthOptions
        {
            Enabled = true,
            MaxFailedAttempts = 5,
            FailureWindowSeconds = 60
        };

        var guard = new BasicAuthAttemptGuard(options, redis: redis);
        await guard.RecordFailureAsync("TESTUSER|127.0.0.1");

        await db.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(s => s.Contains("redis.call('INCR'") && s.Contains("redis.call('EXPIRE'")),
            Arg.Is<StackExchange.Redis.RedisKey[]>(keys => keys.Length == 2 && keys[0].ToString() == "autheris:fail:{TESTUSER|127.0.0.1}"),
            Arg.Is<StackExchange.Redis.RedisValue[]>(vals => vals.Length == 2 && (long)vals[0] == 60 && (int)vals[1] == 5));
    }
}
