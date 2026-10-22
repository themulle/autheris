namespace Autheris.Tests.Unit.Security.Totp;

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.State;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>GOV-09: TOTP attempts are reserved before evaluation; SHA-1 only; fail closed without the shared store.</summary>
[Trait("Category", "Security")]
public class TotpLockoutConcurrencyTests
{
    private const int MaxAttempts = 5;

    private static TotpVerificationService Create(IDistributedClusterStateProvider? state) =>
        new(state, new TotpOptions { MaxFailedAttempts = MaxAttempts, LockoutMinutes = 5 });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelWrongGuesses_EvaluatedAtMostMaxAttempts(bool useClusterStore)
    {
        // Counting fake: every reservation is recorded; evaluation is only allowed for reservations <= limit.
        var inner = new InMemoryClusterStateProvider();
        var reservations = 0;
        IDistributedClusterStateProvider? state = null;
        if (useClusterStore)
        {
            var counting = Substitute.For<IDistributedClusterStateProvider>();
            counting.IncrementAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Interlocked.Increment(ref reservations);
                    return inner.IncrementAsync(call.ArgAt<string>(0), call.ArgAt<long>(1), call.ArgAt<TimeSpan>(2), call.ArgAt<CancellationToken>(3));
                });
            state = counting;
        }

        var totp = Create(state);
        var secret = totp.GenerateEnrollment("user-1", "u@example.com").SecretBase32;
        var valid = totp.GenerateTotpCode(secret);
        var wrong = valid == "000000" ? "111111" : "000000";

        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => totp.VerifyAndConsumeTotpAsync("user-1", secret, wrong))));

        results.ShouldAllBe(r => !r);
        if (useClusterStore)
        {
            reservations.ShouldBe(50);
        }

        // The account is locked: even the valid code is rejected without evaluation.
        (await totp.VerifyAndConsumeTotpAsync("user-1", secret, valid)).ShouldBeFalse();
    }

    [Fact]
    public async Task Sha256Code_Rejected()
    {
        var totp = Create(null);
        var secret = totp.GenerateEnrollment("user-1", "u@example.com").SecretBase32;
        var sha1 = totp.GenerateTotpCode(secret);
        var sha256 = ComputeSha256Code(secret, DateTimeOffset.UtcNow);
        if (sha256 == sha1)
        {
            return; // vanishingly rare collision; the assertion would be meaningless
        }

        totp.VerifyTotp(secret, sha256).ShouldBeFalse();
        (await totp.VerifyAndConsumeTotpAsync("user-1", secret, sha256)).ShouldBeFalse();
    }

    [Fact]
    public async Task ClusterStoreUnavailable_RejectsValidCode()
    {
        var state = Substitute.For<IDistributedClusterStateProvider>();
        state.IncrementAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<long?>((long?)null));
        var totp = Create(state);
        var secret = totp.GenerateEnrollment("user-1", "u@example.com").SecretBase32;

        (await totp.VerifyAndConsumeTotpAsync("user-1", secret, totp.GenerateTotpCode(secret))).ShouldBeFalse();
    }

    [Fact]
    public async Task SuccessAfterFailures_ResetsCounter()
    {
        var totp = Create(new InMemoryClusterStateProvider());
        var secret = totp.GenerateEnrollment("user-1", "u@example.com").SecretBase32;
        var valid = totp.GenerateTotpCode(secret);
        var wrong = valid == "000000" ? "111111" : "000000";

        for (var i = 0; i < MaxAttempts - 1; i++)
        {
            (await totp.VerifyAndConsumeTotpAsync("user-1", secret, wrong)).ShouldBeFalse();
        }

        (await totp.VerifyAndConsumeTotpAsync("user-1", secret, valid)).ShouldBeTrue();

        // Counter was reset: a full set of failures is allowed again before lockout.
        for (var i = 0; i < MaxAttempts - 1; i++)
        {
            (await totp.VerifyAndConsumeTotpAsync("user-1", secret, wrong)).ShouldBeFalse();
        }
    }

    private static string ComputeSha256Code(string secretBase32, DateTimeOffset now)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var key = new System.Collections.Generic.List<byte>();
        foreach (var c in secretBase32.Trim().ToUpperInvariant())
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                key.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        var counter = BitConverter.GetBytes(now.ToUnixTimeSeconds() / 30);
        Array.Reverse(counter);
        var hash = HMACSHA256.HashData(key.ToArray(), counter);
        var offset = hash[^1] & 0x0F;
        var bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (bin % 1_000_000).ToString("D6");
    }
}
