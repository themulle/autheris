namespace Autheris.Tests.Unit.Security;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

public sealed class Api13BasicAuthLockoutAtomicTests
{
    [Fact]
    public async Task RecordFailureAsync_WithRedis_PerformsAtomicIncrementAndExpiresViaLua()
    {
        // SEC-12H-05: Lockout counter in Redis must be atomic via Lua script rather than separate INCR and EXPIRE,
        // preventing orphaned keys without TTL.
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        var db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var options = new BasicAuthOptions { MaxFailedAttempts = 3, FailureWindowSeconds = 60 };
        var guard = new BasicAuthAttemptGuard(options, null, null, redis);

        var key = BasicAuthAttemptGuard.BuildAttemptKey("user", "192.168.1.1");
        await guard.RecordFailureAsync(key);

        // Verify ScriptEvaluateAsync was invoked with atomic Lua script and cluster-safe hash-tagged keys
        await db.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(s => s.Contains("redis.call('INCR'") && s.Contains("redis.call('EXPIRE'")),
            Arg.Is<RedisKey[]>(k => k.Length == 2 && k[0] == "autheris:fail:{USER|192.168.1.1}" && k[1] == "autheris:lockout:{USER|192.168.1.1}"),
            Arg.Is<RedisValue[]>(v => v.Length == 2 && (long)v[0] == 60 && (int)v[1] == 3));
    }

    [Fact]
    public async Task RecordFailureAsync_WithRedis_PassesThresholdToLuaScript()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        var db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var options = new BasicAuthOptions { MaxFailedAttempts = 5, FailureWindowSeconds = 120 };
        var guard = new BasicAuthAttemptGuard(options, null, null, redis);

        var key = BasicAuthAttemptGuard.BuildAttemptKey("user", "192.168.1.1");
        await guard.RecordFailureAsync(key);

        await db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Is<RedisValue[]>(v => v.Length == 2 && (long)v[0] == 120 && (int)v[1] == 5));
    }

    [Fact]
    public async Task IsLockedOutAsync_WithRedis_ChecksKeyExistsAsync()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        var db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        db.KeyExistsAsync("autheris:lockout:{USER|192.168.1.1}").Returns(Task.FromResult(true));

        var options = new BasicAuthOptions { MaxFailedAttempts = 3, FailureWindowSeconds = 60 };
        var guard = new BasicAuthAttemptGuard(options, null, null, redis);

        var isLocked = await guard.IsLockedOutAsync("USER|192.168.1.1");
        isLocked.ShouldBeTrue();
    }

    [Fact]
    public void RedisKeys_UseMatchingHashTags_ToPreventCrossSlotErrors()
    {
        // SR-P2-04 / SR15-11: Keys in multi-key operations (like Lua script or KeyDeleteAsync)
        // must use Redis Hash Tags {...} on the attemptKey to ensure both keys map to the exact same cluster hash slot.
        var attemptKey = "ALICE|10.0.0.1";
        var failKey = BasicAuthAttemptGuard.BuildFailKey(attemptKey);
        var lockoutKey = BasicAuthAttemptGuard.BuildLockoutKey(attemptKey);

        failKey.ShouldBe("autheris:fail:{ALICE|10.0.0.1}");
        lockoutKey.ShouldBe("autheris:lockout:{ALICE|10.0.0.1}");

        // Extract hash tag: substring between first '{' and next '}'
        var failTagStart = failKey.IndexOf('{');
        var failTagEnd = failKey.IndexOf('}', failTagStart);
        var failTag = failKey.Substring(failTagStart + 1, failTagEnd - failTagStart - 1);

        var lockoutTagStart = lockoutKey.IndexOf('{');
        var lockoutTagEnd = lockoutKey.IndexOf('}', lockoutTagStart);
        var lockoutTag = lockoutKey.Substring(lockoutTagStart + 1, lockoutTagEnd - lockoutTagStart - 1);

        failTag.ShouldBe(attemptKey);
        lockoutTag.ShouldBe(attemptKey);
        failTag.ShouldBe(lockoutTag);
    }
}
