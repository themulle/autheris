namespace Autheris.Tests.Unit.Security;

using Autheris.Api.Security;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

/// <summary>Security review E-13 / A-4: per-address failure limit over all users, IPv6 /64 grouping.</summary>
public sealed class BasicAuthSprayProtectionTests
{
    [Fact]
    public async Task PasswordSpraying_OverManyUsers_LocksTheAddress_NotJustThePair()
    {
        var guard = new BasicAuthAttemptGuard(new BasicAuthOptions { MaxFailedAttempts = 3, MaxFailedAttemptsPerIp = 6, FailureWindowSeconds = 300 });
        const string ip = "203.0.113.7";
        var ipKey = BasicAuthAttemptGuard.BuildIpKey(ip);

        for (int i = 0; i < 6; i++)
        {
            // every user is tried once: no (user, IP) pair ever reaches its own limit
            await guard.RecordFailureAsync(BasicAuthAttemptGuard.BuildAttemptKey("user" + i, ip));
            await guard.RecordFailureAsync(ipKey, guard.MaxFailedAttemptsPerIp);
        }

        (await guard.IsLockedOutAsync(BasicAuthAttemptGuard.BuildAttemptKey("user0", ip))).ShouldBeFalse();
        (await guard.IsLockedOutAsync(ipKey)).ShouldBeTrue();
    }

    [Fact]
    public void PerIpLimit_IsNeverBelowThePerUserLimit()
    {
        var guard = new BasicAuthAttemptGuard(new BasicAuthOptions { MaxFailedAttempts = 10, MaxFailedAttemptsPerIp = 2 });

        guard.MaxFailedAttemptsPerIp.ShouldBe(10);
    }

    [Fact]
    public void Ipv6Addresses_OfTheSame64_ShareOneKey()
    {
        var a = BasicAuthAttemptGuard.BuildAttemptKey("alice", "2001:db8:1:2:aaaa:bbbb:cccc:dddd");
        var b = BasicAuthAttemptGuard.BuildAttemptKey("alice", "2001:db8:1:2:1111:2222:3333:4444");
        var other = BasicAuthAttemptGuard.BuildAttemptKey("alice", "2001:db8:1:3::1");

        a.ShouldBe(b);
        a.ShouldNotBe(other);
    }

    [Fact]
    public void Ipv4MappedAddress_IsTreatedAsIpv4()
    {
        BasicAuthAttemptGuard.NormalizeIp("::ffff:192.0.2.5").ShouldBe("192.0.2.5");
        BasicAuthAttemptGuard.NormalizeIp(null).ShouldBe("unknown");
    }
}
