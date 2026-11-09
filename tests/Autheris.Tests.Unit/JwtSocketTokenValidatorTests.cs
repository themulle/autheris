namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity;
using Autheris.Domain.Identity;
using Autheris.Domain.Common;
using Autheris.Tests.Unit.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

public sealed class JwtSocketTokenValidatorTests
{
    private readonly SymmetricSecurityKey _signingKey = new(Encoding.UTF8.GetBytes("a-very-secret-test-key-of-at-least-256-bits-length!"));

    private static IIdentityRealmRegistry Registry(RealmGroupSource groupSource = RealmGroupSource.None)
    {
        var realm = new IdentityRealm(
            "test-realm",
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, FixedTenantId: new TenantId("tenant-test")),
            groupSource: groupSource);
        return new Autheris.Tests.Unit.Security.FakeIdentityRealmRegistry(realm);
    }

    private (JwtSocketTokenValidator Validator, IHostEnvironment Env) CreateValidator(
        bool isDev = false,
        bool enableTestAuth = false,
        IIdentityPipeline? pipeline = null,
        AuthenticationOptions? authentication = null)
    {
        var jwtOptions = new JwtBearerOptions
        {
            TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _signingKey,
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false
            }
        };

        var jwtOptionsMonitor = new Autheris.Tests.Unit.Security.FakeOptionsMonitor<JwtBearerOptions>(jwtOptions);

        var gatewayOptions = Options.Create(new GatewayOptions
        {
            Authentication = authentication ?? new AuthenticationOptions
            {
                EnableTestAuthHandler = enableTestAuth
            }
        });

        var env = new Autheris.Tests.Unit.Security.FakeHostEnvironment(isDev ? Environments.Development : Environments.Production);

        var validator = new JwtSocketTokenValidator(
            jwtOptionsMonitor,
            gatewayOptions,
            env,
            NullLogger<JwtSocketTokenValidator>.Instance,
            new Autheris.Tests.Unit.Security.FakeIdentitySubjectResolver(),
            pipeline ?? TestIdentityPipeline.Create(Registry(), options: gatewayOptions.Value));

        return (validator, env);
    }

    private string GenerateToken(string sub, SecurityKey? key = null, string? issuer = null, params Claim[] extra)
    {
        var keyToUse = key ?? _signingKey;
        var tokenHandler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, sub),
                new Claim("sub", sub),
                new Claim("tenant_id", "tenant-test"),
                .. extra
            ]),
            Issuer = issuer,
            SigningCredentials = new SigningCredentials(keyToUse, SecurityAlgorithms.HmacSha256Signature)
        };
        var token = tokenHandler.CreateToken(descriptor);
        return tokenHandler.WriteToken(token);
    }

    [Fact]
    public async Task ValidateTokenAsync_WithValidSignature_ReturnsValidPrincipal()
    {
        var (validator, _) = CreateValidator(isDev: false);
        var token = GenerateToken("user-alice");

        var (isValid, principal) = await validator.ValidateTokenAsync(token);

        isValid.ShouldBeTrue();
        principal.ShouldNotBeNull();
        principal.FindFirst("sub")?.Value.ShouldBe("user-alice");
        principal.FindFirst("tenant_id")?.Value.ShouldBe("tenant-test");
    }

    [Fact]
    public async Task ValidateTokenAsync_WithForgedSignature_Fails()
    {
        var (validator, _) = CreateValidator(isDev: false);
        var forgedKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("an-entirely-different-secret-key-32-chars-long!"));
        var token = GenerateToken("user-alice", forgedKey);

        var (isValid, principal) = await validator.ValidateTokenAsync(token);

        isValid.ShouldBeFalse();
        principal.ShouldBeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_WithPrivilegedSid_IsRejected()
    {
        var (validator, _) = CreateValidator(isDev: false);
        var token = GenerateToken("S-1-5-32-544"); // Builtin Administrators

        var (isValid, principal) = await validator.ValidateTokenAsync(token);

        isValid.ShouldBeFalse();
        principal.ShouldBeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_EmptyOrWhitespaceToken_ReturnsInvalid()
    {
        var (validator, _) = CreateValidator(isDev: false);

        var (isValid, principal) = await validator.ValidateTokenAsync("");

        isValid.ShouldBeFalse();
        principal.ShouldBeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_InNonDev_WithoutSigningKeys_FailsClosed()
    {
        var jwtOptions = new JwtBearerOptions
        {
            TokenValidationParameters = new TokenValidationParameters()
        };

        var jwtOptionsMonitor = new Autheris.Tests.Unit.Security.FakeOptionsMonitor<JwtBearerOptions>(jwtOptions);

        var gatewayOptions = Options.Create(new GatewayOptions());
        var env = new Autheris.Tests.Unit.Security.FakeHostEnvironment(Environments.Production);

        var validator = new JwtSocketTokenValidator(
            jwtOptionsMonitor,
            gatewayOptions,
            env,
            NullLogger<JwtSocketTokenValidator>.Instance,
            new Autheris.Tests.Unit.Security.FakeIdentitySubjectResolver(),
            TestIdentityPipeline.Create(Registry()));

        var token = GenerateToken("user-bob");

        var (isValid, principal) = await validator.ValidateTokenAsync(token);

        isValid.ShouldBeFalse();
        principal.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Websocket_validator_requires_policy_and_pipeline_dependencies()
    {
        var ctor = typeof(JwtSocketTokenValidator).GetConstructors().Single();
        ctor.GetParameters().ShouldAllBe(p => !p.HasDefaultValue);
        ctor.GetParameters().Select(p => p.ParameterType).ShouldContain(typeof(IIdentitySubjectResolver));
        ctor.GetParameters().Select(p => p.ParameterType).ShouldContain(typeof(IIdentityPipeline));

        var monitor = new Autheris.Tests.Unit.Security.FakeOptionsMonitor<JwtBearerOptions>(new JwtBearerOptions());
        var env = new Autheris.Tests.Unit.Security.FakeHostEnvironment();
        Should.Throw<ArgumentNullException>(() => new JwtSocketTokenValidator(monitor, Options.Create(new GatewayOptions()), env,
            NullLogger<JwtSocketTokenValidator>.Instance, null!, new Autheris.Tests.Unit.Security.FakeIdentityPipeline()));
        Should.Throw<ArgumentNullException>(() => new JwtSocketTokenValidator(monitor, Options.Create(new GatewayOptions()), env,
            NullLogger<JwtSocketTokenValidator>.Instance, new Autheris.Tests.Unit.Security.FakeIdentitySubjectResolver(), null!));
    }

    private static AuthenticationOptions EntraAndAdfs() => new()
    {
        EntraId = new EntraIdAuthOptions { Enabled = true, TenantId = "tid-1", ClientId = "api-client" },
        Adfs = new AdfsAuthOptions { Enabled = true, Authority = "https://adfs.example.com/adfs", AllowedClientIds = ["adfs-client"] }
    };

    [Fact]
    [Trait("Category", "Security")]
    public async Task Websocket_selects_entra_policy_by_issuer()
    {
        var (validator, _) = CreateValidator(authentication: EntraAndAdfs());

        // An Entra-issued token without azp/appid is not an access token for the Entra policy.
        var (isValid, _) = await validator.ValidateTokenAsync(GenerateToken("user-1", issuer: "https://login.microsoftonline.com/tid-1/v2.0"));

        isValid.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Websocket_adfs_token_is_not_checked_against_entra_policy()
    {
        var (validator, _) = CreateValidator(authentication: EntraAndAdfs());

        // The same shape (no azp, no scopes) passes the AD FS policy when the client is on the AD FS allowlist.
        var (isValid, principal) = await validator.ValidateTokenAsync(
            GenerateToken("user-1", issuer: "https://adfs.example.com/adfs", extra: new Claim("appid", "adfs-client")));

        isValid.ShouldBeTrue();
        principal.ShouldNotBeNull();

        var (unlisted, _) = await validator.ValidateTokenAsync(
            GenerateToken("user-1", issuer: "https://adfs.example.com/adfs", extra: new Claim("appid", "other-client")));
        unlisted.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Websocket_closes_when_group_resolution_degrades()
    {
        // A token realm whose groups claim carries a value that is not a canonical SID resolves Degraded: the connection is refused.
        var options = new GatewayOptions();
        var validator = CreateValidator(pipeline: TestIdentityPipeline.Create(Registry(RealmGroupSource.Token), options: options)).Validator;

        var (isValid, principal) = await validator.ValidateTokenAsync(GenerateToken("user-1", extra: new Claim("groups", "not-a-sid")));

        isValid.ShouldBeFalse();
        principal.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Websocket_pipeline_exception_rejects_token()
    {
        var pipeline = new Autheris.Tests.Unit.Security.FakeIdentityPipeline
        {
            Handler = (_, _, _, _) => throw new InvalidOperationException("provider offline")
        };
        var (validator, _) = CreateValidator(pipeline: pipeline);

        var (isValid, principal) = await validator.ValidateTokenAsync(GenerateToken("user-1"));

        isValid.ShouldBeFalse();
        principal.ShouldBeNull();
    }
}
