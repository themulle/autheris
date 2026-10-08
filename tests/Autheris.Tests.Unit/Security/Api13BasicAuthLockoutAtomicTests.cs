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
    public async Task RecordFailureAsync_WithRedis_PerformsAtomicIncrementAndExpires()
    {
        // API-13: Lockout counter in Redis must be atomic (StringIncrementAsync) rather than non-atomic read-modify-write,
        // and must be fully asynchronous to avoid thread pool starvation.
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        var db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        // First failure -> count 1
        db.StringIncrementAsync("autheris:fail:USER|192.168.1.1").Returns(Task.FromResult((long)1));

        var options = new BasicAuthOptions { MaxFailedAttempts = 3, FailureWindowSeconds = 60 };
        var guard = new BasicAuthAttemptGuard(options, null, null, redis);

        var key = BasicAuthAttemptGuard.BuildAttemptKey("user", "192.168.1.1");
        await guard.RecordFailureAsync(key);

        // Verify StringIncrementAsync was used atomically
        await db.Received(1).StringIncrementAsync("autheris:fail:USER|192.168.1.1");
        // Verify expiration set on initial failure
        await db.Received(1).KeyExpireAsync("autheris:fail:USER|192.168.1.1", TimeSpan.FromSeconds(60));
        // Not yet locked out at count 1
        await db.DidNotReceive().StringSetAsync(Arg.Is<RedisKey>(k => k == "autheris:lockout:USER|192.168.1.1"), Arg.Any<RedisValue>(), Arg.Any<Expiration>());
    }

    [Fact]
    public async Task RecordFailureAsync_WithRedis_LocksOutWhenThresholdReached()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        var db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        // 3rd failure -> count 3 (limit = 3)
        db.StringIncrementAsync("autheris:fail:USER|192.168.1.1").Returns(Task.FromResult((long)3));

        var options = new BasicAuthOptions { MaxFailedAttempts = 3, FailureWindowSeconds = 60 };
        var guard = new BasicAuthAttemptGuard(options, null, null, redis);

        var key = BasicAuthAttemptGuard.BuildAttemptKey("user", "192.168.1.1");
        await guard.RecordFailureAsync(key);

        // Threshold reached -> lockout key set
        await db.Received(1).StringSetAsync("autheris:lockout:USER|192.168.1.1", "1", Arg.Any<Expiration>());
    }

    [Fact]
    public async Task IsLockedOutAsync_WithRedis_ChecksKeyExistsAsync()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        var db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        db.KeyExistsAsync("autheris:lockout:USER|192.168.1.1").Returns(Task.FromResult(true));

        var options = new BasicAuthOptions { MaxFailedAttempts = 3, FailureWindowSeconds = 60 };
        var guard = new BasicAuthAttemptGuard(options, null, null, redis);

        var isLocked = await guard.IsLockedOutAsync("USER|192.168.1.1");
        isLocked.ShouldBeTrue();
    }
}
