using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class AuthenticationSecurityTests
{
    private static IOptions<GatewayOptions> CreateGatewayOptions(BasicAuthOptions? basicAuth = null)
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = basicAuth ?? new BasicAuthOptions
                {
                    Enabled = true,
                    Realm = "TestRealm",
                    Users =
                    [
                        new BasicAuthUserConfig
                        {
                            Username = "alice",
                            Password = "secretPassword123",
                            Sid = "S-1-5-21-1111-ALICE",
                            Roles = ["FinanceReader", "GovernanceAdmin"],
                            GroupSids = ["S-1-5-21-GROUPS-100"]
                        },
                        new BasicAuthUserConfig
                        {
                            Username = "bob_hashed",
                            Password = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hashedSecret456"))),
                            Sid = "S-1-5-21-2222-BOB",
                            Roles = ["HrReader"],
                            GroupSids = ["S-1-5-21-GROUPS-200"]
                        }
                    ]
                }
            }
        };

        return Options.Create(options);
    }

    private static (BasicAuthenticationHandler Handler, DefaultHttpContext Context) CreateHandler(
        IOptions<GatewayOptions> options,
        Microsoft.AspNetCore.Hosting.IWebHostEnvironment? env = null)
    {
        if (env == null)
        {
            var mockEnv = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            mockEnv.EnvironmentName.Returns("Development");
            env = mockEnv;
        }

        var schemeOptionsMonitor = new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        var handler = new BasicAuthenticationHandler(
            schemeOptionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            options,
            env);

        var context = new DefaultHttpContext();
        handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.Basic, "Basic", typeof(BasicAuthenticationHandler)), context).GetAwaiter().GetResult();
        return (handler, context);
    }

    [Fact]
    public async Task BasicAuth_InProduction_PlaintextPassword_FailsAuthentication()
    {
        var mockEnv = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var (handler, context) = CreateHandler(CreateGatewayOptions(), env: mockEnv);
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secretPassword123"));
        context.Request.Headers.Authorization = $"Basic {creds}";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task BasicAuth_ValidPlaintextPassword_AuthenticatesSuccessfully()
    {
        var (handler, context) = CreateHandler(CreateGatewayOptions());
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secretPassword123"));
        context.Request.Headers.Authorization = $"Basic {creds}";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Principal.ShouldNotBeNull();
        result.Principal.Identity?.Name.ShouldBe("alice");

        var sid = result.Principal.GetUserSid();
        sid.ShouldNotBeNull();
        sid.Value.Value.ShouldBe("S-1-5-21-1111-ALICE");

        var roles = result.Principal.GetUserRoles();
        roles.ShouldContain("FinanceReader");
        roles.ShouldContain("GovernanceAdmin");

        var groups = result.Principal.GetGroupSids();
        groups.Select(g => g.Value).ShouldContain("S-1-5-21-GROUPS-100");
    }

    [Fact]
    public async Task BasicAuth_ValidHashedPassword_AuthenticatesSuccessfully()
    {
        var (handler, context) = CreateHandler(CreateGatewayOptions());
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("bob_hashed:hashedSecret456"));
        context.Request.Headers.Authorization = $"Basic {creds}";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Principal.ShouldNotBeNull();
        result.Principal.Identity?.Name.ShouldBe("bob_hashed");

        var sid = result.Principal.GetUserSid();
        sid.ShouldNotBeNull();
        sid.Value.Value.ShouldBe("S-1-5-21-2222-BOB");
        result.Principal.GetUserRoles().ShouldContain("HrReader");
    }

    [Fact]
    public async Task BasicAuth_InvalidPassword_Fails()
    {
        var (handler, context) = CreateHandler(CreateGatewayOptions());
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:wrongPassword"));
        context.Request.Headers.Authorization = $"Basic {creds}";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure?.Message.ShouldContain("Invalid username or password");
    }

    [Fact]
    public async Task BasicAuth_UnknownUser_Fails()
    {
        var (handler, context) = CreateHandler(CreateGatewayOptions());
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("unknownUser:anyPassword"));
        context.Request.Headers.Authorization = $"Basic {creds}";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure?.Message.ShouldContain("Invalid username or password");
    }

    [Fact]
    public async Task BasicAuth_Challenge_Sets401AndWwwAuthenticateHeader()
    {
        var (handler, context) = CreateHandler(CreateGatewayOptions());

        await handler.ChallengeAsync(new AuthenticationProperties());

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        context.Response.Headers.WWWAuthenticate.ToString().ShouldContain("Basic realm=\"TestRealm\"");
    }

    [Fact]
    public async Task BasicAuth_Disabled_ReturnsNoResult()
    {
        var disabledOptions = CreateGatewayOptions(new BasicAuthOptions { Enabled = false });
        var (handler, context) = CreateHandler(disabledOptions);
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secretPassword123"));
        context.Request.Headers.Authorization = $"Basic {creds}";

        var result = await handler.AuthenticateAsync();

        result.None.ShouldBeTrue();
    }

    [Fact]
    public async Task EnterpriseClaimsTransformation_NormalizesEntraIdClaims()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.AddClaim(new Claim("oid", "00000000-0000-0000-0000-000000000001"));
        identity.AddClaim(new Claim("preferred_username", "user@corp.onmicrosoft.com"));
        identity.AddClaim(new Claim("groups", "azure-ad-group-uuid-123"));
        identity.AddClaim(new Claim("roles", "SecurityAuditor"));

        var principal = new ClaimsPrincipal(identity);
        var transformer = new EnterpriseClaimsTransformation();

        var transformed = await transformer.TransformAsync(principal);

        var userSid = transformed.GetUserSid();
        userSid.ShouldNotBeNull();
        userSid.Value.Value.ShouldBe("00000000-0000-0000-0000-000000000001");

        transformed.FindFirst(ClaimTypes.PrimarySid)?.Value.ShouldBe("00000000-0000-0000-0000-000000000001");
        transformed.GetGroupSids().Select(g => g.Value).ShouldContain("azure-ad-group-uuid-123");
        transformed.GetUserRoles().ShouldContain("SecurityAuditor");
    }

    [Fact]
    public async Task EnterpriseClaimsTransformation_NormalizesAdfsClaims()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.AddClaim(new Claim("primarysid", "S-1-5-21-ADFS-USER-999"));
        identity.AddClaim(new Claim("groupsid", "S-1-5-21-ADFS-GROUP-888"));
        identity.AddClaim(new Claim("role", "DataSteward"));

        var principal = new ClaimsPrincipal(identity);
        var transformer = new EnterpriseClaimsTransformation();

        var transformed = await transformer.TransformAsync(principal);

        var userSid = transformed.GetUserSid();
        userSid.ShouldNotBeNull();
        userSid.Value.Value.ShouldBe("S-1-5-21-ADFS-USER-999");

        transformed.FindFirst(ClaimTypes.PrimarySid)?.Value.ShouldBe("S-1-5-21-ADFS-USER-999");
        transformed.GetGroupSids().Select(g => g.Value).ShouldContain("S-1-5-21-ADFS-GROUP-888");
        transformed.GetUserRoles().ShouldContain("DataSteward");
    }

    [Fact]
    public void ClaimsPrincipalExtensions_SupportsAllIdentityProviderClaims()
    {
        // 1. Windows Kerberos / Negotiate
        var krb = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-KRB")]));
        krb.GetUserSid()?.Value.ShouldBe("S-1-5-21-KRB");

        // 2. Entra ID / Azure AD onprem_sid
        var entraSync = new ClaimsPrincipal(new ClaimsIdentity([new Claim("onprem_sid", "S-1-5-21-ENTRA-SYNC")]));
        entraSync.GetUserSid()?.Value.ShouldBe("S-1-5-21-ENTRA-SYNC");

        // 3. Entra ID Cloud OID
        var entraCloud = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "entra-guid-1234")]));
        entraCloud.GetUserSid()?.Value.ShouldBe("entra-guid-1234");

        // 4. AD FS PrimarySid
        var adfs = new ClaimsPrincipal(new ClaimsIdentity([new Claim("primarysid", "S-1-5-21-ADFS-1234")]));
        adfs.GetUserSid()?.Value.ShouldBe("S-1-5-21-ADFS-1234");

        // 5. Basic Auth NameIdentifier
        var basic = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "basic_user")]));
        basic.GetUserSid()?.Value.ShouldBe("basic_user");
    }

    private static (ForwardAuthAuthenticationHandler Handler, DefaultHttpContext Context) CreateForwardAuthHandler(
        GatewayOptions options,
        Autheris.Application.Interfaces.IKeyVaultSecretProvider? secretProvider = null,
        Microsoft.AspNetCore.Hosting.IWebHostEnvironment? env = null)
    {
        if (env == null)
        {
            var mockEnv = NSubstitute.Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            mockEnv.EnvironmentName.Returns("Development");
            env = mockEnv;
        }

        var schemeOptionsMonitor = new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        var validator = new TrustedProxyValidator(Options.Create(options));
        var handler = new ForwardAuthAuthenticationHandler(
            schemeOptionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            Options.Create(options),
            validator,
            secretProvider,
            env);

        var context = new DefaultHttpContext();
        handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ForwardAuth, "ForwardAuth", typeof(ForwardAuthAuthenticationHandler)), context).GetAwaiter().GetResult();
        return (handler, context);
    }

    [Fact]
    public async Task ForwardAuth_WhenFromTrustedProxy_AuthenticatesSuccessfully()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true,
                    RequireTrustedProxy = true,
                    TrustedProxies = ["10.0.0.1"],
                    // RR-L2-02: raw upstream SIDs and custom roles require explicit opt-in
                    TrustUpstreamGroupSids = true,
                    AllowedRoles = ["FinanceReader", "Auditor"]
                }
            }
        };

        var (handler, context) = CreateForwardAuthHandler(options);
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");
        context.Request.Headers["X-Forwarded-User"] = "k8s_service_user";
        context.Request.Headers["X-Forwarded-Groups"] = "S-1-5-21-K8S-GRP1,S-1-5-21-K8S-GRP2";
        context.Request.Headers["X-Forwarded-Roles"] = "FinanceReader,Auditor";
        context.Request.Headers["X-Forwarded-Email"] = "k8s@corp.local";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Principal.ShouldNotBeNull();
        result.Principal.Identity?.Name.ShouldBe("k8s_service_user");
        result.Principal.GetUserSid()?.Value.ShouldBe("S-1-5-21-FORWARD-K8S_SERVICE_USER");
        result.Principal.GetGroupSids().Select(g => g.Value).ShouldContain("S-1-5-21-K8S-GRP1");
        result.Principal.GetUserRoles().ShouldContain("FinanceReader");
    }

    [Fact]
    public async Task RR_L2_02_ForwardAuth_Defaults_NamespaceGroups_AndDropNonAllowlistedOrAdminRoles()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true,
                    RequireTrustedProxy = true,
                    TrustedProxies = ["10.0.0.1"],
                    AllowedRoles = ["Analyst", "GatewayAdmin", "PlatformAdmin"] // admin entries must still be ignored
                }
            }
        };

        var (handler, context) = CreateForwardAuthHandler(options);
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");
        context.Request.Headers["X-Forwarded-User"] = "alice";
        context.Request.Headers["X-Forwarded-Groups"] = "S-1-5-21-1004336348-1177238915-682003330-512,finance";
        context.Request.Headers["X-Forwarded-Roles"] = "Analyst,DataOwner,GatewayAdmin,PlatformAdmin,ClusterAdmin,tenant-a:ClusterAdmin";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        var groups = result.Principal!.GetGroupSids().Select(g => g.Value).ToList();
        groups.ShouldNotContain("S-1-5-21-1004336348-1177238915-682003330-512");
        groups.ShouldContain("S-1-5-21-FORWARD-GRP-S-1-5-21-1004336348-1177238915-682003330-512");
        groups.ShouldContain("S-1-5-21-FORWARD-GRP-finance");

        var roles = result.Principal.GetUserRoles();
        roles.ShouldContain("Analyst");
        roles.ShouldNotContain("DataOwner");
        roles.ShouldNotContain("GatewayAdmin");
        roles.ShouldNotContain("PlatformAdmin");
        roles.ShouldNotContain("ClusterAdmin");
        roles.ShouldNotContain("tenant-a:ClusterAdmin");
    }

    [Fact]
    public async Task RR_L2_02_ForwardAuth_TenantNotInAllowlist_IsRejected()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true,
                    RequireTrustedProxy = true,
                    TrustedProxies = ["10.0.0.1"],
                    AllowedTenantIds = ["tenant-a"]
                }
            }
        };

        var (handler, context) = CreateForwardAuthHandler(options);
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");
        context.Request.Headers["X-Forwarded-User"] = "mallory";
        context.Request.Headers["X-Forwarded-Tenant"] = "tenant-b";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task ForwardAuth_WhenFromUntrustedProxy_RejectsWithSecurityFailure()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true,
                    RequireTrustedProxy = true,
                    TrustedProxies = ["10.0.0.1"]
                }
            }
        };

        var (handler, context) = CreateForwardAuthHandler(options);
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.100");
        context.Request.Headers["X-Forwarded-User"] = "impersonated_admin";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure?.Message.ShouldContain("Untrusted proxy IP");
    }

    [Fact]
    public async Task ForwardAuth_WhenOriginalTcpRemoteIpInItemsIsTrusted_AuthenticatesSuccessfully()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true,
                    RequireTrustedProxy = true,
                    TrustedProxies = ["10.0.0.1"]
                }
            }
        };

        var (handler, context) = CreateForwardAuthHandler(options);
        // Simulate UseForwardedHeaders having changed RemoteIpAddress to an untrusted client
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("1.2.3.4");
        // But original TCP remote IP was buffered in context.Items["OriginalTcpRemoteIp"]
        context.Items["OriginalTcpRemoteIp"] = System.Net.IPAddress.Parse("10.0.0.1");
        context.Request.Headers["X-Forwarded-User"] = "k8s_service_user";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Principal.ShouldNotBeNull();
        result.Principal.Identity?.Name.ShouldBe("k8s_service_user");
    }

    [Fact]
    public async Task ForwardAuth_WhenOriginalTcpRemoteIpInItemsIsUntrusted_RejectsEvenIfConnectionRemoteIpIsTrusted()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true,
                    RequireTrustedProxy = true,
                    TrustedProxies = ["10.0.0.1"]
                }
            }
        };

        var (handler, context) = CreateForwardAuthHandler(options);
        // Simulate spoofed Connection.RemoteIpAddress being trusted
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");
        // But the actual physical TCP connection came from an attacker
        context.Items["OriginalTcpRemoteIp"] = System.Net.IPAddress.Parse("192.168.1.100");
        context.Request.Headers["X-Forwarded-User"] = "impersonated_admin";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure?.Message.ShouldContain("Untrusted proxy IP");
    }

    [Fact]
    public async Task ForwardAuth_WhenDisabled_ReturnsNoResult()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions { Enabled = false }
            }
        };

        var (handler, context) = CreateForwardAuthHandler(options);
        context.Request.Headers["X-Forwarded-User"] = "admin";

        var result = await handler.AuthenticateAsync();
        result.None.ShouldBeTrue();
    }

    [Fact]
    public async Task BasicAuth_WhenPasswordIsPbkdf2Hashed_AuthenticatesSuccessfully()
    {
        var salt = "randomSalt123456"u8.ToArray();
        var saltB64 = Convert.ToBase64String(salt);
        var derived = Rfc2898DeriveBytes.Pbkdf2("mySecureP@ss!", salt, 10_000, HashAlgorithmName.SHA256, 32);
        var hashB64 = Convert.ToBase64String(derived);
        var pbkdf2String = $"$pbkdf2$10000${saltB64}${hashB64}";

        var options = CreateGatewayOptions(new BasicAuthOptions
        {
            Enabled = true,
            Users =
            [
                new BasicAuthUserConfig
                {
                    Username = "charlie",
                    Password = pbkdf2String,
                    Sid = "S-1-5-21-3333-CHARLIE",
                    Roles = ["FinanceReader"]
                }
            ]
        });

        var (handler, context) = CreateHandler(options);
        var rawCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("charlie:mySecureP@ss!"));
        context.Request.Headers.Authorization = $"Basic {rawCredentials}";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Principal.ShouldNotBeNull();
        result.Principal.Identity?.Name.ShouldBe("charlie");
        result.Principal.FindFirst(ClaimTypes.PrimarySid)?.Value.ShouldBe("S-1-5-21-3333-CHARLIE");
    }

    [Fact]
    public async Task BasicAuth_WhenUserNotFound_PerformsTimingMitigationAndFails()
    {
        var options = CreateGatewayOptions();
        var (handler, context) = CreateHandler(options);
        var rawCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("nonexistentUser:anyPassword!"));
        context.Request.Headers.Authorization = $"Basic {rawCredentials}";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure?.Message.ShouldBe("Invalid username or password.");
    }

    [Fact]
    public async Task ForwardAuth_InNonDevelopmentWithoutSharedSecret_FailsAuthentication()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true,
                    RequireTrustedProxy = false, // Even if proxy check is relaxed
                    SharedSecret = null,
                    SharedSecretKeyVaultRef = null
                }
            }
        };

        var mockEnv = NSubstitute.Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var (handler, context) = CreateForwardAuthHandler(options, env: mockEnv);
        context.Request.Headers["X-Forwarded-User"] = "admin";

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure?.Message.ShouldContain("Outside of Development");
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public T CurrentValue { get; }
        public TestOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
