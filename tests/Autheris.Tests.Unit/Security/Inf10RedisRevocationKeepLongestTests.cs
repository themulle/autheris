namespace Autheris.Tests.Unit.Security;

using System;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

/// <summary>
/// INF-10: a later revocation must never shorten the Redis TTL of an earlier, longer one (atomic max via script).
/// </summary>
public sealed class Inf10RedisRevocationKeepLongestTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task INF_10_RevokeAsync_UsesAtomicKeepLongestScript_NotPlainSet()
    {
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        var store = new RedisTokenRevocationService(multiplexer, Options.Create(new GatewayOptions()), NullLogger<RedisTokenRevocationService>.Instance);

        await store.RevokeAsync("S-1-5-21-INF10", DateTimeOffset.UtcNow.AddMinutes(15));

        await db.Received(1).ScriptEvaluateAsync(
            RedisTokenRevocationService.KeepLongestRevocationScript,
            Arg.Any<RedisKey[]?>(),
            Arg.Any<RedisValue[]?>(),
            Arg.Any<CommandFlags>());
    }
}
