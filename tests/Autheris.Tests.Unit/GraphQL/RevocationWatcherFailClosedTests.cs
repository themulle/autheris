namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.GraphQL.Subscriptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

/// <summary>SR-L-18: revocation watchers terminate a session whose revocation status cannot be verified.</summary>
[Trait("Category", "Security")]
public sealed class RevocationWatcherFailClosedTests
{
    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity([new Claim("jti", "j-1")], "TestAuth"));

    private sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    private static ITokenRevocationService ThrowingService(Exception ex)
    {
        var svc = Substitute.For<ITokenRevocationService>();
        svc.IsRevokedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<bool>(ex));
        return svc;
    }

    private static (WebSocketAuthInterceptor.RevocationWatch Watch, RecordingLifetimeFeature Lifetime) CreateWatch(
        ITokenRevocationService svc, RevocationWatchPolicy? policy = null)
    {
        var ctx = new DefaultHttpContext();
        var lifetime = new RecordingLifetimeFeature();
        ctx.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var watch = new WebSocketAuthInterceptor.RevocationWatch(
            ctx, Principal, svc, NullLogger.Instance, policy ?? new RevocationWatchPolicy(3, true));
        return (watch, lifetime);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebSocketWatch_ServiceThrowsNTimes_AbortsConnection(bool unavailableException)
    {
        Exception ex = unavailableException ? new TokenRevocationUnavailableException() : new InvalidOperationException("boom");
        var (watch, lifetime) = CreateWatch(ThrowingService(ex));
        using (watch)
        {
            (await watch.CheckAsync()).ShouldBeFalse();
            (await watch.CheckAsync()).ShouldBeFalse();
            lifetime.Aborted.ShouldBeFalse();

            (await watch.CheckAsync()).ShouldBeTrue();
            lifetime.Aborted.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task WebSocketWatch_FailClosedDisabled_NeverAborts()
    {
        var (watch, lifetime) = CreateWatch(ThrowingService(new InvalidOperationException("boom")), new RevocationWatchPolicy(1, false));
        using (watch)
        {
            for (var i = 0; i < 5; i++)
            {
                (await watch.CheckAsync()).ShouldBeFalse();
            }

            lifetime.Aborted.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task WebSocketWatch_SingleTransientError_DoesNotAbort_AndSuccessResetsCounter()
    {
        var svc = Substitute.For<ITokenRevocationService>();
        var calls = 0;
        svc.IsRevokedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                // fail, fail, succeed, fail, fail, succeed: never three in a row
                return calls % 3 == 0 ? ValueTask.FromResult(false) : ValueTask.FromException<bool>(new InvalidOperationException("transient"));
            });
        var (watch, lifetime) = CreateWatch(svc);
        using (watch)
        {
            for (var i = 0; i < 6; i++)
            {
                (await watch.CheckAsync()).ShouldBeFalse();
            }

            lifetime.Aborted.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Subscription_ServiceThrowsNTimes_CancelsStream()
    {
        using var cts = new CancellationTokenSource();
        var counter = new RevocationFailureCounter(new RevocationWatchPolicy(3, true));
        var svc = ThrowingService(new TokenRevocationUnavailableException());

        await Subscription.CheckRevocationAsync(cts, svc, Principal, counter);
        await Subscription.CheckRevocationAsync(cts, svc, Principal, counter);
        cts.IsCancellationRequested.ShouldBeFalse();

        await Subscription.CheckRevocationAsync(cts, svc, Principal, counter);
        cts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Subscription_SingleTransientError_DoesNotCancel_AndSuccessResetsCounter()
    {
        using var cts = new CancellationTokenSource();
        var counter = new RevocationFailureCounter(new RevocationWatchPolicy(3, true));
        var failing = ThrowingService(new InvalidOperationException("transient"));
        var ok = Substitute.For<ITokenRevocationService>();
        ok.IsRevokedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(false));

        await Subscription.CheckRevocationAsync(cts, failing, Principal, counter);
        await Subscription.CheckRevocationAsync(cts, failing, Principal, counter);
        await Subscription.CheckRevocationAsync(cts, ok, Principal, counter);
        counter.ConsecutiveFailures.ShouldBe(0);
        await Subscription.CheckRevocationAsync(cts, failing, Principal, counter);
        await Subscription.CheckRevocationAsync(cts, failing, Principal, counter);

        cts.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task Subscription_Revoked_CancelsImmediately()
    {
        using var cts = new CancellationTokenSource();
        var svc = Substitute.For<ITokenRevocationService>();
        svc.IsRevokedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));

        await Subscription.CheckRevocationAsync(cts, svc, Principal, new RevocationFailureCounter());

        cts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Policy_FromOptions_ClampsThresholdAndDefaultsFailClosed()
    {
        RevocationWatchPolicy.From((RevocationWatchOptions?)null).ShouldBe(new RevocationWatchPolicy(3, true));
        RevocationWatchPolicy.From(new RevocationWatchOptions { RevocationCheckMaxConsecutiveFailures = 0 }).MaxConsecutiveFailures.ShouldBe(1);
    }
}
