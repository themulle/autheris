namespace Autheris.Tests.Unit.Cache;

using System;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Infrastructure.Serialization;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WAVE3-TIME-SC-02: the L2 envelope expiry follows the injected clock; expired when now is strictly past the frame expiry.</summary>
public sealed class ConsentCacheL2ExpiryClockTests
{
    [Fact]
    public void ConsentCache_L2EnvelopeExpired_IsMiss()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("cluster-master-key"));
        var options = new GatewayOptions();
        using var cache = new ConsentCacheService(
            new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<IEpochValidationService>(),
            Substitute.For<IEventBus>(),
            Options.Create(options),
            new MemoryPackCacheSerializer(),
            null,
            secrets,
            NullLogger<ConsentCacheService>.Instance,
            timeProvider: clock);
        var payload = Encoding.UTF8.GetBytes("p");
        var framed = cache.ProtectL2Payload("k", payload, clock.GetUtcNow().AddMinutes(5));

        cache.UnprotectL2Payload("k", framed).ShouldBe(payload);

        clock.Advance(TimeSpan.FromMinutes(5));
        cache.UnprotectL2Payload("k", framed).ShouldBe(payload, "not expired exactly at the frame expiry (strictly-greater semantics unchanged)");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        cache.UnprotectL2Payload("k", framed).ShouldBeNull();
    }
}
