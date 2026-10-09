using System.Security;
using System.Security.Claims;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>Review G2: E-1 (revocation claim mapping), E-2 (Kerberos only), tenant claim, session header, hash CLI.</summary>
[Collection("ConsoleTests")]
public sealed class AuthSessionHardeningTests
{
    private const string OidUri = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    [Fact]
    public void E1_MappedJwtPrincipal_YieldsSubjectLookupKeys()
    {
        // As produced by JwtBearer with MapInboundClaims=true: sub -> NameIdentifier, oid -> objectidentifier URI.
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "sub-123"),
                new Claim(OidUri, "oid-456"),
                new Claim("jti", "jti-789")
            ],
            "Bearer");
        var keys = TokenRevocationKeys.GetLookupKeys(new ClaimsPrincipal(identity));

        keys.ShouldContain(TokenRevocationKeys.Normalize("sub-123"));
        keys.ShouldContain(TokenRevocationKeys.Normalize("oid-456"));
        keys.ShouldContain(TokenRevocationKeys.Normalize("jti-789"));
    }

    [Fact]
    public void E1_UnmappedAndTenantScopedKeys()
    {
        var identity = new ClaimsIdentity(
            [new Claim("sub", "s1"), new Claim("oid", "o1"), new Claim("tenant_id", "acme")],
            "Bearer");
        var keys = TokenRevocationKeys.GetLookupKeys(new ClaimsPrincipal(identity));

        keys.ShouldContain(TokenRevocationKeys.Normalize("s1"));
        keys.ShouldContain(TokenRevocationKeys.Normalize("o1"));
        keys.ShouldContain(TokenRevocationKeys.TenantScoped(new Autheris.Domain.Common.TenantId("acme"), "o1"));
    }

    [Fact]
    public async Task E2_NegotiateOptions_DisablePersistence_AndRejectNtlmWhenKerberosOnly()
    {
        var options = new NegotiateOptions();
        NegotiateHardening.Configure(options, requireKerberosOnly: true);

        options.PersistNtlmCredentials.ShouldBeFalse();
        options.PersistKerberosCredentials.ShouldBeFalse();
        var onAuthenticated = options.Events.ShouldNotBeNull().OnAuthenticated.ShouldNotBeNull();

        AuthenticatedContext Ctx(string authType) =>
            new(new DefaultHttpContext(), new AuthenticationScheme("Negotiate", null, typeof(NegotiateHandler)), options)
            {
                Principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], authType))
            };

        var ntlm = Ctx("NTLM");
        await onAuthenticated(ntlm);
        ntlm.Result?.Succeeded.ShouldNotBe(true);
        ntlm.Result?.Failure.ShouldNotBeNull();

        var kerberos = Ctx("Kerberos");
        await onAuthenticated(kerberos);
        kerberos.Result.ShouldBeNull();
    }

    [Fact]
    public void E2_WithoutKerberosOnly_NoRejectionHandler()
    {
        var options = new NegotiateOptions();
        NegotiateHardening.Configure(options, requireKerberosOnly: false);
        options.PersistNtlmCredentials.ShouldBeFalse();
        options.PersistKerberosCredentials.ShouldBeFalse();
    }

    [Fact]
    public void MalformedTenantClaim_FailsRequest()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-1-2-3-1001"), new Claim("tenant_id", "bad tenant!;")],
                "Bearer"))
        };

        Should.Throw<SecurityException>(() => SecurityContextFactory.CreateFromHttpContext(context));
    }

    [Fact]
    public void ValidateUsers_RejectsMalformedTenantId()
    {
        var basic = new BasicAuthOptions
        {
            Enabled = true,
            Users = [new BasicAuthUserConfig { Username = "a", Password = "x", Sid = "S-1-5-21-1", TenantId = "bad tenant!" }]
        };
        BasicAuthSession.ValidateUsers(basic).ShouldContain(e => e.Contains("TenantId"));

        var ok = new BasicAuthOptions
        {
            Enabled = true,
            Users = [new BasicAuthUserConfig { Username = "a", Password = "x", Sid = "S-1-5-21-1", TenantId = "acme" }]
        };
        BasicAuthSession.ValidateUsers(ok).ShouldBeEmpty();
    }

    [Fact]
    public void NoSessionHeader_IsOutsideStrippedPrefix()
    {
        BasicAuthSession.NoSessionHeader.StartsWith("x-autheris-", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    [Fact]
    public void HashCli_ReadsStdin_DefaultsToPbkdf2With600kIterations()
    {
        var originalOut = Console.Out;
        var originalIn = Console.In;
        var stdout = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetIn(new StringReader("Secret123!\n"));
            PasswordHashCli.Run(["hash-password"]).ShouldBe(0);
            var output = stdout.ToString();
            output.ShouldContain("$pbkdf2$600000$");
            output.ShouldNotContain("argon2id");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public void HashCli_WithoutPassword_Fails()
    {
        var originalIn = Console.In;
        var originalErr = Console.Error;
        try
        {
            Console.SetIn(new StringReader(string.Empty));
            Console.SetError(new StringWriter());
            PasswordHashCli.Run(["hash-password"]).ShouldBe(1);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetError(originalErr);
        }
    }
}
