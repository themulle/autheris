namespace Autheris.Tests.Unit.State;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.State;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-A-09 consumer audit: TOTP never treats an unavailable shared store as "absent" or "not consumed".</summary>
[Trait("Category", "Security")]
public class TotpClusterStateUnavailableTests
{
    private static IDistributedClusterStateProvider UnavailableProvider()
    {
        var state = Substitute.For<IDistributedClusterStateProvider>();
        state.GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ClusterStateUnavailableException("get", "k", null));
        state.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Throws(new ClusterStateUnavailableException("set", "k", null));
        state.IncrementAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<long?>((long?)null));
        return state;
    }

    [Fact]
    public async Task TotpSecretStore_WhenStateUnavailable_Denies()
    {
        var store = new ClusterTotpSecretStore(UnavailableProvider());

        await Should.ThrowAsync<ClusterStateUnavailableException>(async () => await store.GetSecretAsync("user-1"));
        await Should.ThrowAsync<ClusterStateUnavailableException>(async () => await store.HasSecretAsync("user-1"));
    }

    [Fact]
    public async Task TotpSecretStore_WhenKeyAbsent_ReturnsNull()
    {
        var store = new ClusterTotpSecretStore(new InMemoryClusterStateProvider());

        (await store.GetSecretAsync("user-1")).ShouldBeNull();
        (await store.HasSecretAsync("user-1")).ShouldBeFalse();
    }

    [Fact]
    public async Task TotpVerification_WhenReplayStoreUnavailable_RejectsValidCode()
    {
        var totp = new TotpVerificationService(UnavailableProvider());
        var secret = totp.GenerateEnrollment("user-1", "u@example.com").SecretBase32;
        var code = totp.GenerateTotpCode(secret);

        (await totp.VerifyAndConsumeTotpAsync("user-1", secret, code)).ShouldBeFalse();
    }

    [Fact]
    public async Task TotpVerification_WhenReplayStoreHealthy_AcceptsValidCodeOnce()
    {
        var totp = new TotpVerificationService(new InMemoryClusterStateProvider());
        var secret = totp.GenerateEnrollment("user-2", "u@example.com").SecretBase32;
        var code = totp.GenerateTotpCode(secret);

        (await totp.VerifyAndConsumeTotpAsync("user-2", secret, code)).ShouldBeTrue();
        (await totp.VerifyAndConsumeTotpAsync("user-2", secret, code)).ShouldBeFalse();
    }
}
