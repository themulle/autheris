namespace Autheris.Tests.Unit.Persistence;

using System.Security;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class SqlLoginCircuitBreakerTests
{
    private static DataSourceConnectionOptions Source(string password = "pw-ref-1", int cooldown = 300) => new()
    {
        Provider = "SqlServer",
        ConnectionString = "Server=h,1433;Database=D;Integrated Security=true;",
        LoginFailureCooldownSeconds = cooldown,
        WindowsAuth = new SqlServerWindowsAuthOptions { Mode = SqlServerWindowsAuthMode.Ntlm, Domain = "DOM", Username = "svc", PasswordRef = password }
    };

    private static SqlServerWindowsAuthException LoginRejected() =>
        new(SqlServerWindowsAuthFailureCategory.LoginRejected, "rejected", sqlNumber: 18452);

    [Fact]
    public void LoginFailure_OpensBreaker_AndBlocksWithoutContactingServer()
    {
        var time = new FakeTimeProvider();
        var breaker = new SqlLoginCircuitBreaker(time);

        breaker.RecordFailure(Source(), LoginRejected()).ShouldBeTrue();

        var blocked = Should.Throw<SqlSourceLoginBlockedException>(() => breaker.ThrowIfOpen(Source()));
        blocked.CategoryName.ShouldBe("LoginBlocked");
        blocked.Remaining.ShouldBeGreaterThan(TimeSpan.FromSeconds(299));
        blocked.Message.ShouldNotContain("pw-ref-1");
    }

    [Fact]
    public void Breaker_ClosesAfterCooldown()
    {
        var time = new FakeTimeProvider();
        var breaker = new SqlLoginCircuitBreaker(time);
        breaker.RecordFailure(Source(), LoginRejected());

        time.Advance(TimeSpan.FromSeconds(301));

        Should.NotThrow(() => breaker.ThrowIfOpen(Source()));
    }

    [Fact]
    public void ChangedCredentialIdentity_LiftsBlock()
    {
        var breaker = new SqlLoginCircuitBreaker(new FakeTimeProvider());
        breaker.RecordFailure(Source("pw-ref-1"), LoginRejected());

        Should.NotThrow(() => breaker.ThrowIfOpen(Source("pw-ref-2")));
        Should.Throw<SqlSourceLoginBlockedException>(() => breaker.ThrowIfOpen(Source("pw-ref-1")));
    }

    [Fact]
    public void Success_ClearsBreaker()
    {
        var breaker = new SqlLoginCircuitBreaker(new FakeTimeProvider());
        breaker.RecordFailure(Source(), LoginRejected());

        breaker.RecordSuccess(Source());

        Should.NotThrow(() => breaker.ThrowIfOpen(Source()));
    }

    [Fact]
    public void CooldownZero_DisablesBreaker()
    {
        var breaker = new SqlLoginCircuitBreaker(new FakeTimeProvider());

        breaker.RecordFailure(Source(cooldown: 0), LoginRejected()).ShouldBeFalse();
        Should.NotThrow(() => breaker.ThrowIfOpen(Source(cooldown: 0)));
    }

    [Theory]
    [InlineData("InvalidCredentials", true)]
    [InlineData("UnknownCredentials", true)]
    [InlineData("CredentialsExpired", true)]
    [InlineData("InvalidToken", false)]
    [InlineData("GenericFailure", false)]
    public void NtlmNegotiation_OnlyCredentialStatusesOpenBreaker(string status, bool opens)
    {
        var failure = new SqlServerWindowsAuthException(SqlServerWindowsAuthFailureCategory.NtlmNegotiation, "x", ntlmStatus: status);

        SqlLoginCircuitBreaker.IsLoginFailure(failure, out _).ShouldBe(opens);
    }

    [Theory]
    [InlineData(SqlServerWindowsAuthFailureCategory.GssapiUnavailable)]
    [InlineData(SqlServerWindowsAuthFailureCategory.KerberosNegotiation)]
    public void GssAndKerberosFailures_DoNotOpenBreaker(SqlServerWindowsAuthFailureCategory category)
    {
        SqlLoginCircuitBreaker.IsLoginFailure(new SqlServerWindowsAuthException(category, "x"), out _).ShouldBeFalse();
    }

    [Fact]
    public void NetworkAndTimeoutFailures_DoNotOpenBreaker()
    {
        var breaker = new SqlLoginCircuitBreaker(new FakeTimeProvider());

        breaker.RecordFailure(Source(), new TimeoutException("t")).ShouldBeFalse();
        breaker.RecordFailure(Source(), new InvalidOperationException("network", new IOException("reset"))).ShouldBeFalse();
        Should.NotThrow(() => breaker.ThrowIfOpen(Source()));
    }

    [Fact]
    public void LoginFailureWrappedAsInner_IsDetected()
    {
        SqlLoginCircuitBreaker.IsLoginFailure(new InvalidOperationException("outer", LoginRejected()), out string reason).ShouldBeTrue();
        reason.ShouldContain("18452");
    }

    [Fact]
    public void GssIdentityRegistry_AllowsSameAccount_RefusesDifferentAccount()
    {
        var registry = new GssIdentityRegistry();
        registry.Claim("DOM", "svc");
        Should.NotThrow(() => registry.Claim("dom", "SVC"));

        var ex = Should.Throw<SecurityException>(() => registry.Claim("DOM", "other"));
        ex.Message.ShouldContain("managed NTLM");
        ex.Message.ShouldNotContain("svc");
    }

    [Fact]
    public void Describer_ReportsEffectiveLoginWithoutPassword()
    {
        var describer = new SqlSourceDescriber();

        describer.Describe(Source()).LoginName.ShouldBe("DOM\\svc");
        describer.Describe(new DataSourceConnectionOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=h;User ID=sa_reader;Password=Hunter2;Database=D"
        }).LoginName.ShouldBe("sa_reader");
        describer.Describe(new DataSourceConnectionOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=h;Integrated Security=true;Database=D"
        }).LoginName.ShouldBe("(ambient credentials)");
    }

    [Fact]
    public void ManagedNtlmProvider_PoolKeyIdentity_FollowsCredentials()
    {
        var a = new ManagedNtlmSspiContextProvider(new System.Net.NetworkCredential("svc", "pw", "DOM"));
        var same = new ManagedNtlmSspiContextProvider(new System.Net.NetworkCredential("svc", "pw", "DOM"));
        var otherPassword = new ManagedNtlmSspiContextProvider(new System.Net.NetworkCredential("svc", "pw2", "DOM"));
        var otherUser = new ManagedNtlmSspiContextProvider(new System.Net.NetworkCredential("svc2", "pw", "DOM"));

        a.Equals(same).ShouldBeTrue();
        a.GetHashCode().ShouldBe(same.GetHashCode());
        a.Equals(otherPassword).ShouldBeFalse();
        a.Equals(otherUser).ShouldBeFalse();
    }

    [Fact]
    public void MaterializeNtlmUserFile_UnchangedEntry_IsNotRewrittenAndKeepsMode()
    {
        string dir = Path.Combine(Path.GetTempPath(), "autheris-ntlm-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = SqlServerWindowsAuthConfigurator.MaterializeNtlmUserFile("DOM", "svc", "pw", dir);
            var firstWrite = File.GetLastWriteTimeUtc(path);
            Thread.Sleep(20);

            SqlServerWindowsAuthConfigurator.MaterializeNtlmUserFile("DOM", "svc", "pw", dir).ShouldBe(path);

            File.GetLastWriteTimeUtc(path).ShouldBe(firstWrite);
            File.ReadAllText(path).ShouldBe("DOM:svc:pw\n");
            if (OperatingSystem.IsLinux())
            {
                File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            SqlServerWindowsAuthConfigurator.MaterializeNtlmUserFile("DOM", "svc", "pw2", dir);
            File.ReadAllText(path).ShouldBe("DOM:svc:pw2\n");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
