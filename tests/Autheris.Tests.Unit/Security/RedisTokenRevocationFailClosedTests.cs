#pragma warning disable CS0618 // StackExchange.Redis exception constructors without CommandFlags are fine for test doubles
namespace Autheris.Tests.Unit.Security;

using System;
using System.Globalization;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using StackExchange.Redis;
using Xunit;

/// <summary>SR-L-10: optional fail-closed token revocation lookup.</summary>
[Trait("Category", "Security")]
public class RedisTokenRevocationFailClosedTests
{
    private static ClaimsPrincipal Principal(string sid, DateTimeOffset issuedAt) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, sid),
                new Claim("iat", issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            ],
            "TestAuth"));

    private static IConnectionMultiplexer DisconnectedMultiplexer()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>())
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis down"));
        return multiplexer;
    }

    private static RedisTokenRevocationService Create(
        IConnectionMultiplexer multiplexer, bool? failClosed, int graceSeconds = 60, bool development = false)
    {
        var options = new GatewayOptions
        {
            Caching = new CachingOptions
            {
                Redis = new RedisOptions
                {
                    FailClosedOnRevocationLookupError = failClosed,
                    RevocationLookupGraceSeconds = graceSeconds
                }
            }
        };
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(development ? "Development" : "Production");
        return new RedisTokenRevocationService(
            multiplexer, Options.Create(options), NullLogger<RedisTokenRevocationService>.Instance, environment: env);
    }

    [Fact]
    public async Task IsRevokedAsync_RedisDisconnected_FailClosedOn_Throws()
    {
        var store = Create(DisconnectedMultiplexer(), failClosed: true);

        await Should.ThrowAsync<TokenRevocationUnavailableException>(async () =>
            await store.IsRevokedAsync(Principal("S-1-5-21-FC-1", DateTimeOffset.UtcNow.AddMinutes(-10))));
    }

    [Fact]
    public async Task IsRevokedAsync_RedisDisconnected_FailClosedOff_ReturnsFalse()
    {
        var store = Create(DisconnectedMultiplexer(), failClosed: false);

        (await store.IsRevokedAsync(Principal("S-1-5-21-FC-2", DateTimeOffset.UtcNow.AddMinutes(-10)))).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task IsRevokedAsync_DefaultPolicy_FollowsEnvironment(bool development, bool expectedFailClosed)
    {
        var store = Create(DisconnectedMultiplexer(), failClosed: null, development: development);
        var principal = Principal("S-1-5-21-FC-3", DateTimeOffset.UtcNow.AddMinutes(-10));

        if (expectedFailClosed)
        {
            await Should.ThrowAsync<TokenRevocationUnavailableException>(async () => await store.IsRevokedAsync(principal));
        }
        else
        {
            (await store.IsRevokedAsync(principal)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task IsRevokedAsync_TokenWithinGrace_Allowed()
    {
        var store = Create(DisconnectedMultiplexer(), failClosed: true, graceSeconds: 60);

        (await store.IsRevokedAsync(Principal("S-1-5-21-FC-4", DateTimeOffset.UtcNow.AddSeconds(-5)))).ShouldBeFalse();
    }

    [Fact]
    public async Task IsRevokedAsync_TokenOlderThanGrace_Throws()
    {
        var store = Create(DisconnectedMultiplexer(), failClosed: true, graceSeconds: 60);

        await Should.ThrowAsync<TokenRevocationUnavailableException>(async () =>
            await store.IsRevokedAsync(Principal("S-1-5-21-FC-5", DateTimeOffset.UtcNow.AddSeconds(-120))));
    }

    [Fact]
    public async Task IsRevokedAsync_GraceZero_NeverAllowsOnFailure()
    {
        var store = Create(DisconnectedMultiplexer(), failClosed: true, graceSeconds: 0);

        await Should.ThrowAsync<TokenRevocationUnavailableException>(async () =>
            await store.IsRevokedAsync(Principal("S-1-5-21-FC-6", DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task IsRevokedAsync_LocalRevocationStillWins_WhenRedisDown()
    {
        var store = Create(DisconnectedMultiplexer(), failClosed: true);
        await Should.ThrowAsync<RedisConnectionException>(() => store.RevokeAsync("S-1-5-21-FC-7", DateTimeOffset.UtcNow.AddHours(1)));

        (await store.IsRevokedAsync(Principal("S-1-5-21-FC-7", DateTimeOffset.UtcNow.AddMinutes(-5)))).ShouldBeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_SingleKeyLookupFaults_FailClosedOn_Throws()
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromException<RedisValue>(new RedisTimeoutException("timeout", CommandStatus.Unknown)));
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        var store = Create(multiplexer, failClosed: true);

        await Should.ThrowAsync<TokenRevocationUnavailableException>(async () =>
            await store.IsRevokedAsync(Principal("S-1-5-21-FC-8", DateTimeOffset.UtcNow.AddMinutes(-10))));
    }

    [Fact]
    public async Task Middleware_UnavailableException_Returns503WithRetryAfter()
    {
        var revocation = Substitute.For<ITokenRevocationService>();
        revocation.IsRevokedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<System.Threading.CancellationToken>())
            .Throws(new TokenRevocationUnavailableException());
        var nextCalled = false;
        var middleware = new TokenRevocationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext
        {
            User = Principal("S-1-5-21-FC-9", DateTimeOffset.UtcNow.AddMinutes(-10))
        };
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, revocation);

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers["Retry-After"].ToString().ShouldBe("5");
        context.Response.Body.Position = 0;
        var body = new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
        body.ShouldContain("TOKEN_REVOCATION_UNAVAILABLE");
    }

    [Fact]
    public async Task Middleware_RevokedToken_StillReturns401()
    {
        var revocation = Substitute.For<ITokenRevocationService>();
        revocation.IsRevokedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new ValueTask<bool>(true));
        var middleware = new TokenRevocationMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext { User = Principal("S-1-5-21-FC-10", DateTimeOffset.UtcNow) };
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, revocation);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }
}
