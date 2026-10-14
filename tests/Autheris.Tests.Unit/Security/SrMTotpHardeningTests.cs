namespace Autheris.Tests.Unit.Security;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.State;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public class SrMTotpHardeningTests
{
    private const string Sid = "user-sr-m";

    private static (TotpVerificationService Totp, ClusterTotpSecretStore Store, TotpEnrollmentService Enrollment, InMemoryClusterStateProvider Cluster)
        Create(TotpOptions? options = null, bool useCluster = true)
    {
        var cluster = new InMemoryClusterStateProvider();
        IDistributedClusterStateProvider? state = useCluster ? cluster : null;
        var totp = new TotpVerificationService(state, options);
        var store = new ClusterTotpSecretStore(state);
        var enrollment = new TotpEnrollmentService(totp, store, state, options);
        return (totp, store, enrollment, cluster);
    }

    // ---- SR-M-04 atomic replay consumption ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SR_M_04_ConcurrentConsumption_OfSameCode_HasExactlyOneWinner(bool useCluster)
    {
        var (totp, _, _, _) = Create(useCluster: useCluster);
        var secret = totp.GenerateEnrollment(Sid, "u@example.com").SecretBase32;
        var code = totp.GenerateTotpCode(secret);

        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => totp.VerifyAndConsumeTotpAsync(Sid, secret, code))));

        results.Count(r => r).ShouldBe(1);
    }

    [Fact]
    public async Task SR_M_04_TwoNodes_SharingClusterState_OnlyOneConsumesTheStep()
    {
        var cluster = new InMemoryClusterStateProvider();
        var nodeA = new TotpVerificationService(cluster);
        var nodeB = new TotpVerificationService(cluster);
        var secret = nodeA.GenerateEnrollment(Sid, "u@example.com").SecretBase32;
        var code = nodeA.GenerateTotpCode(secret);

        var results = await Task.WhenAll(
            nodeA.VerifyAndConsumeTotpAsync(Sid, secret, code),
            nodeB.VerifyAndConsumeTotpAsync(Sid, secret, code));

        results.Count(r => r).ShouldBe(1);
    }

    // ---- SR-M-05 throttling ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SR_M_05_AfterMaxFailures_ValidCodeIsRejected(bool useCluster)
    {
        var (totp, _, _, _) = Create(new TotpOptions { MaxFailedAttempts = 3, LockoutMinutes = 15 }, useCluster);
        var secret = totp.GenerateEnrollment(Sid, "u@example.com").SecretBase32;

        for (var i = 0; i < 3; i++)
        {
            (await totp.VerifyAndConsumeTotpAsync(Sid, secret, "000000")).ShouldBeFalse();
        }

        var valid = totp.GenerateTotpCode(secret);
        (await totp.VerifyAndConsumeTotpAsync(Sid, secret, valid)).ShouldBeFalse();
    }

    [Fact]
    public async Task SR_M_05_Lockout_IsPerUser()
    {
        var (totp, _, _, _) = Create(new TotpOptions { MaxFailedAttempts = 2, LockoutMinutes = 15 });
        var secret = totp.GenerateEnrollment("other", "o@example.com").SecretBase32;

        await totp.VerifyAndConsumeTotpAsync(Sid, secret, "000000");
        await totp.VerifyAndConsumeTotpAsync(Sid, secret, "000000");

        (await totp.VerifyAndConsumeTotpAsync("other", secret, totp.GenerateTotpCode(secret))).ShouldBeTrue();
    }

    [Fact]
    public async Task SR_M_05_SuccessBelowThreshold_ResetsCounter()
    {
        var (totp, _, _, _) = Create(new TotpOptions { MaxFailedAttempts = 3, LockoutMinutes = 15 });
        var secret = totp.GenerateEnrollment(Sid, "u@example.com").SecretBase32;

        await totp.VerifyAndConsumeTotpAsync(Sid, secret, "000000");
        await totp.VerifyAndConsumeTotpAsync(Sid, secret, "000000");
        (await totp.VerifyAndConsumeTotpAsync(Sid, secret, totp.GenerateTotpCode(secret))).ShouldBeTrue();

        // counter was reset: two more failures do not lock the user out
        await totp.VerifyAndConsumeTotpAsync(Sid, secret, "000000");
        await totp.VerifyAndConsumeTotpAsync(Sid, secret, "000000");
        var previousStepCode = totp.GenerateTotpCode(secret, DateTimeOffset.UtcNow.AddSeconds(-30));
        (await totp.VerifyAndConsumeTotpAsync(Sid, secret, previousStepCode)).ShouldBeTrue();
    }

    // ---- SR-M-06 revocation / SR-L-05 encryption ----

    [Fact]
    public async Task SR_M_06_RevocationOnOneNode_IsEffectiveOnOtherNode()
    {
        var cluster = new InMemoryClusterStateProvider();
        var nodeA = new ClusterTotpSecretStore(cluster);
        var nodeB = new ClusterTotpSecretStore(cluster);

        await nodeA.SetSecretAsync(Sid, "JBSWY3DPEHPK3PXP");
        (await nodeB.GetSecretAsync(Sid)).ShouldBe("JBSWY3DPEHPK3PXP");

        await nodeA.RemoveSecretAsync(Sid);

        (await nodeB.GetSecretAsync(Sid)).ShouldBeNull();
        (await nodeB.HasSecretAsync(Sid)).ShouldBeFalse();
    }

    [Fact]
    public async Task SR_M_06_OverwriteOnOneNode_IsEffectiveOnOtherNode()
    {
        var cluster = new InMemoryClusterStateProvider();
        var nodeA = new ClusterTotpSecretStore(cluster);
        var nodeB = new ClusterTotpSecretStore(cluster);

        await nodeA.SetSecretAsync(Sid, "JBSWY3DPEHPK3PXP");
        await nodeB.GetSecretAsync(Sid);
        await nodeA.SetSecretAsync(Sid, "MFRGGZDFMZTWQ2LK");

        (await nodeB.GetSecretAsync(Sid)).ShouldBe("MFRGGZDFMZTWQ2LK");
    }

    [Fact]
    public async Task SR_M_06_ClusterFailure_DoesNotFallBackToStaleLocalSecret()
    {
        var cluster = Substitute.For<IDistributedClusterStateProvider>();
        cluster.GetAsync<string>(Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(_ => ValueTask.FromException<string?>(new InvalidOperationException("down")));
        var store = new ClusterTotpSecretStore(cluster);

        await Should.ThrowAsync<InvalidOperationException>(() => store.GetSecretAsync(Sid));
    }

    [Fact]
    public async Task SR_L_05_Secret_IsStoredEncrypted_AndRoundTrips()
    {
        var cluster = new InMemoryClusterStateProvider();
        var protector = Substitute.For<ITotpSecretProtector>();
        protector.Protect(Arg.Any<string>()).Returns(ci => "enc(" + ci.Arg<string>() + ")");
        protector.Unprotect(Arg.Any<string>()).Returns(ci => ci.Arg<string>()["enc(".Length..^1]);
        var store = new ClusterTotpSecretStore(cluster, protector);

        await store.SetSecretAsync(Sid, "JBSWY3DPEHPK3PXP");

        var raw = await cluster.GetAsync<string>($"totp:secret:{Sid}");
        raw.ShouldNotBeNull();
        raw.ShouldNotContain("JBSWY3DPEHPK3PXP", Case.Sensitive);
        raw.ShouldStartWith(ClusterTotpSecretStore.ProtectedPrefix);
        (await store.GetSecretAsync(Sid)).ShouldBe("JBSWY3DPEHPK3PXP");
    }

    // ---- SR-M-01 enrollment ----

    [Fact]
    public async Task SR_M_01_Verify_WithoutPendingEnrollment_IsRejected_EvenIfCodeMatchesAnyClientSeed()
    {
        var (totp, store, enrollment, _) = Create(useCluster: false);
        var attackerSeed = totp.GenerateEnrollment(Sid, "a@example.com").SecretBase32;
        var attackerCode = totp.GenerateTotpCode(attackerSeed);

        var outcome = await enrollment.CompleteEnrollmentAsync(Sid, attackerCode, null);

        outcome.ShouldBe(TotpEnrollmentOutcome.NoPendingEnrollment);
        (await store.HasSecretAsync(Sid)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SR_M_01_Verify_OnlyAcceptsCodeForServerPendingSeed(bool useCluster)
    {
        var (totp, store, enrollment, _) = Create(useCluster: useCluster);
        var begun = await enrollment.BeginEnrollmentAsync(Sid, "u@example.com", "Autheris");
        begun.ShouldNotBeNull();

        var attackerSeed = totp.GenerateEnrollment(Sid, "a@example.com").SecretBase32;
        var attackerCode = totp.GenerateTotpCode(attackerSeed);
        (await enrollment.CompleteEnrollmentAsync(Sid, attackerCode, null)).ShouldBe(TotpEnrollmentOutcome.InvalidCode);
        (await store.HasSecretAsync(Sid)).ShouldBeFalse();

        var good = totp.GenerateTotpCode(begun.SecretBase32);
        (await enrollment.CompleteEnrollmentAsync(Sid, good, null)).ShouldBe(TotpEnrollmentOutcome.Completed);
        (await store.GetSecretAsync(Sid)).ShouldBe(begun.SecretBase32);

        // pending seed is single use
        (await enrollment.CompleteEnrollmentAsync(Sid, good, null)).ShouldBe(TotpEnrollmentOutcome.NoPendingEnrollment);
    }

    [Fact]
    public async Task SR_M_01_PendingSeed_IsBoundToTheUser()
    {
        var (totp, store, enrollment, _) = Create();
        var begun = await enrollment.BeginEnrollmentAsync("victim", "v@example.com", "Autheris");

        var code = totp.GenerateTotpCode(begun!.SecretBase32);
        (await enrollment.CompleteEnrollmentAsync("attacker", code, null)).ShouldBe(TotpEnrollmentOutcome.NoPendingEnrollment);
        (await store.HasSecretAsync("attacker")).ShouldBeFalse();
    }

    [Fact]
    public async Task SR_M_01_Reenrollment_RequiresValidCodeFromCurrentSecret()
    {
        var (totp, store, enrollment, _) = Create();
        var current = totp.GenerateEnrollment(Sid, "u@example.com").SecretBase32;
        await store.SetSecretAsync(Sid, current);

        var begun = await enrollment.BeginEnrollmentAsync(Sid, "u@example.com", "Autheris");
        var newCode = totp.GenerateTotpCode(begun!.SecretBase32);

        (await enrollment.CompleteEnrollmentAsync(Sid, newCode, null)).ShouldBe(TotpEnrollmentOutcome.CurrentCodeRequired);
        (await enrollment.CompleteEnrollmentAsync(Sid, newCode, "000000")).ShouldBe(TotpEnrollmentOutcome.InvalidCurrentCode);
        (await store.GetSecretAsync(Sid)).ShouldBe(current);

        var currentCode = totp.GenerateTotpCode(current);
        (await enrollment.CompleteEnrollmentAsync(Sid, newCode, currentCode)).ShouldBe(TotpEnrollmentOutcome.Completed);
        (await store.GetSecretAsync(Sid)).ShouldBe(begun.SecretBase32);
    }

    [Fact]
    public async Task SR_M_01_BeginEnrollment_FailsClosed_WhenPendingStoreUnavailable()
    {
        var cluster = Substitute.For<IDistributedClusterStateProvider>();
        cluster.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(_ => ValueTask.FromException(new InvalidOperationException("down")));
        var totp = new TotpVerificationService(cluster);
        var enrollment = new TotpEnrollmentService(totp, new ClusterTotpSecretStore(), cluster);

        (await enrollment.BeginEnrollmentAsync(Sid, "u@example.com", "Autheris")).ShouldBeNull();
    }

    [Fact]
    public async Task SR_M_01_Verify_FailsClosed_WhenPendingStoreUnavailable()
    {
        var cluster = Substitute.For<IDistributedClusterStateProvider>();
        cluster.GetAsync<string>(Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(_ => ValueTask.FromException<string?>(new InvalidOperationException("down")));
        var totp = new TotpVerificationService(cluster);
        var enrollment = new TotpEnrollmentService(totp, new ClusterTotpSecretStore(), cluster);

        (await enrollment.CompleteEnrollmentAsync(Sid, "123456", null)).ShouldBe(TotpEnrollmentOutcome.StoreUnavailable);
    }
}
