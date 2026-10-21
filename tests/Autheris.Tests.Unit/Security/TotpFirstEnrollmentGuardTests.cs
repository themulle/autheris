namespace Autheris.Tests.Unit.Security;

using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security review (TOTP trust on first use): a user's first TOTP enrollment requires a fresh interactive sign-in
/// (auth_time, else iat) outside Development; re-enrollment stays protected by the current code.
/// </summary>
public class TotpFirstEnrollmentGuardTests
{
    private const string Sid = "S-1-5-21-APPROVER";
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal User(params Claim[] extra) => UserWithAuthType("Bearer", extra);

    private static ClaimsPrincipal UserWithAuthType(string authenticationType, params Claim[] extra)
    {
        var claims = new System.Collections.Generic.List<Claim> { new(ClaimTypes.PrimarySid, Sid) };
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    private static Claim UnixClaim(string type, DateTimeOffset at) =>
        new(type, at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64);

    private static (DefaultHttpContext Context, IAuditWriter Audit) CreateContext(ClaimsPrincipal user, string environment = "Production", int maxAgeMinutes = 5)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        var audit = Substitute.For<IAuditWriter>();

        var services = new ServiceCollection();
        services.AddSingleton(env);
        services.AddSingleton(audit);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddSingleton(Options.Create(new GatewayOptions { Totp = new TotpOptions { FirstEnrollmentMaxAuthAgeMinutes = maxAgeMinutes } }));

        var context = new DefaultHttpContext { User = user, RequestServices = services.BuildServiceProvider() };
        return (context, audit);
    }

    private static ITotpEnrollmentService Enrollment(bool hasActiveSecret)
    {
        var enrollment = Substitute.For<ITotpEnrollmentService>();
        enrollment.HasActiveSecretAsync(Sid, Arg.Any<CancellationToken>()).Returns(hasActiveSecret);
        return enrollment;
    }

    private static int? StatusOf(IResult? result) => (result as IStatusCodeHttpResult)?.StatusCode;

    [Fact]
    public void AuthTime_TakesPrecedenceOverIat()
    {
        var user = User(UnixClaim("auth_time", Now.AddMinutes(-2)), UnixClaim("iat", Now.AddMinutes(-60)));

        TotpFirstEnrollmentGuard.GetAuthenticationTime(user).ShouldBe(Now.AddMinutes(-2));
        TotpFirstEnrollmentGuard.IsFreshAuthentication(user, TimeSpan.FromMinutes(5), Now).ShouldBeTrue();
    }

    [Fact]
    public void Iat_IsUsed_WhenAuthTimeIsMissing()
    {
        TotpFirstEnrollmentGuard.IsFreshAuthentication(User(UnixClaim("iat", Now.AddMinutes(-4))), TimeSpan.FromMinutes(5), Now).ShouldBeTrue();
        TotpFirstEnrollmentGuard.IsFreshAuthentication(User(UnixClaim("iat", Now.AddMinutes(-6))), TimeSpan.FromMinutes(5), Now).ShouldBeFalse();
    }

    [Fact]
    public void MissingOrFutureTimestamps_AreNotFresh()
    {
        TotpFirstEnrollmentGuard.IsFreshAuthentication(User(), TimeSpan.FromMinutes(5), Now).ShouldBeFalse();
        TotpFirstEnrollmentGuard.IsFreshAuthentication(User(new Claim("auth_time", "not-a-number")), TimeSpan.FromMinutes(5), Now).ShouldBeFalse();
        TotpFirstEnrollmentGuard.IsFreshAuthentication(User(UnixClaim("auth_time", Now.AddMinutes(10))), TimeSpan.FromMinutes(5), Now).ShouldBeFalse();
    }

    [Fact]
    public async Task FirstEnrollment_WithStaleAuthentication_IsRejectedWith403_AndAudited()
    {
        var (context, audit) = CreateContext(User(UnixClaim("auth_time", Now.AddHours(-3))));

        var check = await TotpFirstEnrollmentGuard.CheckAsync(context, Enrollment(hasActiveSecret: false), Sid, "verify", CancellationToken.None);

        check.IsFirstEnrollment.ShouldBeTrue();
        StatusOf(check.Rejection).ShouldBe(StatusCodes.Status403Forbidden);
        await audit.Received(1).RecordBestEffortAsync(
            Arg.Is<AuditLogEntry>(e => e.Decision == "DENY" && e.EventType == AuditEventTypes.TwoFactorEnrollmentVerified),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FirstEnrollment_WithoutAnyAuthenticationTime_IsRejected()
    {
        var (context, _) = CreateContext(User());

        var check = await TotpFirstEnrollmentGuard.CheckAsync(context, Enrollment(hasActiveSecret: false), Sid, "begin", CancellationToken.None);

        StatusOf(check.Rejection).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task FirstEnrollment_WithFreshAuthentication_IsAllowed()
    {
        var (context, audit) = CreateContext(User(UnixClaim("auth_time", Now.AddMinutes(-1))));

        var check = await TotpFirstEnrollmentGuard.CheckAsync(context, Enrollment(hasActiveSecret: false), Sid, "begin", CancellationToken.None);

        check.IsFirstEnrollment.ShouldBeTrue();
        check.Rejection.ShouldBeNull();
        await audit.DidNotReceive().RecordBestEffortAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FirstEnrollment_MaxAgeIsConfigurable()
    {
        var (context, _) = CreateContext(User(UnixClaim("auth_time", Now.AddMinutes(-20))), maxAgeMinutes: 30);

        var check = await TotpFirstEnrollmentGuard.CheckAsync(context, Enrollment(hasActiveSecret: false), Sid, "begin", CancellationToken.None);

        check.Rejection.ShouldBeNull();
    }

    [Fact]
    public async Task ReEnrollment_IsNotSubjectToFreshness_TheCurrentCodeProtectsIt()
    {
        var (context, _) = CreateContext(User(UnixClaim("auth_time", Now.AddDays(-1))));

        var check = await TotpFirstEnrollmentGuard.CheckAsync(context, Enrollment(hasActiveSecret: true), Sid, "verify", CancellationToken.None);

        check.IsFirstEnrollment.ShouldBeFalse();
        check.Rejection.ShouldBeNull();
    }

    [Fact]
    public async Task Development_SkipsFreshnessCheck_ForPersonaLogin()
    {
        var (context, _) = CreateContext(User(), environment: Environments.Development);

        var check = await TotpFirstEnrollmentGuard.CheckAsync(context, Enrollment(hasActiveSecret: false), Sid, "begin", CancellationToken.None);

        check.IsFirstEnrollment.ShouldBeTrue();
        check.Rejection.ShouldBeNull();
    }

    [Fact]
    public async Task SecretStoreFailure_FailsClosedWith503()
    {
        var (context, _) = CreateContext(User(UnixClaim("auth_time", Now)));
        var enrollment = Substitute.For<ITotpEnrollmentService>();
        enrollment.HasActiveSecretAsync(Sid, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("down"));

        var check = await TotpFirstEnrollmentGuard.CheckAsync(context, enrollment, Sid, "begin", CancellationToken.None);

        StatusOf(check.Rejection).ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Theory]
    [InlineData("Kerberos")]
    [InlineData("Negotiate")]
    [InlineData("NTLM")]
    [InlineData("Basic")]
    public async Task PerRequestAuthentication_WithoutTimestamps_IsAllowed_AndAudited(string authenticationType)
    {
        var (context, audit) = CreateContext(UserWithAuthType(authenticationType));

        var check = await TotpFirstEnrollmentGuard.CheckAsync(context, Enrollment(hasActiveSecret: false), Sid, "begin", CancellationToken.None);

        TotpFirstEnrollmentGuard.ClassifyAuthentication(context.User).ShouldBe(TotpEnrollmentAuthKind.PerRequest);
        check.IsFirstEnrollment.ShouldBeTrue();
        check.Rejection.ShouldBeNull();
        await audit.Received(1).RecordBestEffortAsync(
            Arg.Is<AuditLogEntry>(e => e.Decision == "ALLOW" && e.EventType == AuditEventTypes.TwoFactorEnrollmentStarted),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("AuthenticationTypes.Federation")] // JwtBearer identity (IdentityModel default authentication type)
    [InlineData("Bearer")]
    [InlineData("BearerAdfs")]
    public async Task JwtIdentity_WithStaleAuthentication_IsDenied(string authenticationType)
    {
        var (context, _) = CreateContext(UserWithAuthType(authenticationType, UnixClaim("iat", Now.AddHours(-1))));

        var check = await TotpFirstEnrollmentGuard.CheckAsync(context, Enrollment(hasActiveSecret: false), Sid, "verify", CancellationToken.None);

        TotpFirstEnrollmentGuard.ClassifyAuthentication(context.User).ShouldBe(TotpEnrollmentAuthKind.ReplayableToken);
        StatusOf(check.Rejection).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task BasicSessionCookie_WithStaleLogin_IsDenied_FreshLoginIsAllowed()
    {
        var (stale, _) = CreateContext(UserWithAuthType(Autheris.Api.Security.BasicAuthSession.SchemeName, UnixClaim("iat", Now.AddMinutes(-30))));
        var (fresh, _) = CreateContext(UserWithAuthType(Autheris.Api.Security.BasicAuthSession.SchemeName, UnixClaim("iat", Now.AddMinutes(-1))));

        var staleCheck = await TotpFirstEnrollmentGuard.CheckAsync(stale, Enrollment(hasActiveSecret: false), Sid, "begin", CancellationToken.None);
        var freshCheck = await TotpFirstEnrollmentGuard.CheckAsync(fresh, Enrollment(hasActiveSecret: false), Sid, "begin", CancellationToken.None);

        StatusOf(staleCheck.Rejection).ShouldBe(StatusCodes.Status403Forbidden);
        freshCheck.Rejection.ShouldBeNull();
    }

    [Fact]
    public async Task UnknownAuthenticationType_FailsClosed_EvenWithFreshTimestamp()
    {
        var (context, audit) = CreateContext(UserWithAuthType("SomeCustomScheme", UnixClaim("auth_time", Now)));

        var check = await TotpFirstEnrollmentGuard.CheckAsync(context, Enrollment(hasActiveSecret: false), Sid, "begin", CancellationToken.None);

        TotpFirstEnrollmentGuard.ClassifyAuthentication(context.User).ShouldBe(TotpEnrollmentAuthKind.Unknown);
        StatusOf(check.Rejection).ShouldBe(StatusCodes.Status403Forbidden);
        await audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e => e.Decision == "DENY"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompletedFirstEnrollment_IsAudited()
    {
        var (context, audit) = CreateContext(User(UnixClaim("auth_time", Now)));

        await TotpFirstEnrollmentGuard.RecordFirstEnrollmentCompletedAsync(context, Sid, CancellationToken.None);

        await audit.Received(1).RecordBestEffortAsync(
            Arg.Is<AuditLogEntry>(e => e.Decision == "ALLOW" && e.ActorSid.Value == Sid && e.DetailsJson.Contains("firstEnrollment")),
            Arg.Any<CancellationToken>());
    }
}
