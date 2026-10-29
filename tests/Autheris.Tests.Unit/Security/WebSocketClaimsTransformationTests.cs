namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Identity;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Autheris.GraphQL.Subscriptions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WebSocketClaimsTransformationTests
{
    private readonly SymmetricSecurityKey _signingKey = new(Encoding.UTF8.GetBytes("a-very-secret-test-key-of-at-least-256-bits-length!"));

    private JwtSocketTokenValidator CreateValidator(
        IClaimsTransformation? claimsTransformation = null,
        IIdentitySubjectResolver? subjectResolver = null)
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

        var jwtOptionsMonitor = Substitute.For<IOptionsMonitor<JwtBearerOptions>>();
        jwtOptionsMonitor.Get(Arg.Any<string>()).Returns(jwtOptions);

        var gatewayOptions = Options.Create(new GatewayOptions());

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);

        return new JwtSocketTokenValidator(
            jwtOptionsMonitor,
            gatewayOptions,
            env,
            NullLogger<JwtSocketTokenValidator>.Instance,
            subjectResolver,
            claimsTransformation);
    }

    private string GenerateToken(
        string sub,
        string? tenantId = "tenant-test",
        string? issuer = null,
        IEnumerable<Claim>? additionalClaims = null)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, sub),
            new("sub", sub)
        };

        if (tenantId != null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        if (additionalClaims != null)
        {
            claims.AddRange(additionalClaims);
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = issuer,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256Signature)
        };
        var token = tokenHandler.CreateToken(descriptor);
        return tokenHandler.WriteToken(token);
    }

    [Fact]
    public async Task SocketToken_WithClientRealmClaim_RealmStrippedAndIssuerRealmApplied()
    {
        // Arrange: Token includes a spoofed autheris:realm claim and an issuer
        const string issuer = "https://sts.corp.example.com/";
        var token = GenerateToken(
            sub: "user-1",
            tenantId: "tenant-a",
            issuer: issuer,
            additionalClaims: new[]
            {
                new Claim("autheris:realm", "malicious-realm"),
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER1")
            });

        var realmRegistry = Substitute.For<IIdentityRealmRegistry>();
        var corpRealm = new IdentityRealm(
            "corp-realm",
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, FixedTenantId: new TenantId("tenant-a")),
            issuers: new[] { issuer });

        realmRegistry.ResolveRealmByIssuer(issuer).Returns(corpRealm);

        var transformer = new EnterpriseClaimsTransformation(realmRegistry);
        var validator = CreateValidator(claimsTransformation: transformer);

        // Act
        var (isValid, principal) = await validator.ValidateTokenAsync(token);

        // Assert: Valid, client-supplied realm stripped, authoritative issuer realm applied
        isValid.ShouldBeTrue();
        principal.ShouldNotBeNull();

        var realmClaims = principal.FindAll("autheris:realm").ToList();
        realmClaims.Count.ShouldBe(1);
        realmClaims[0].Value.ShouldBe("corp-realm");
    }

    [Fact]
    public async Task SocketToken_ResolverRealm_GetsRealmRoles()
    {
        // Arrange: Realm uses Resolver group source, role resolver provides roles
        const string issuer = "https://sts.resolver.example.com/";
        var token = GenerateToken(
            sub: "analyst-1",
            tenantId: "tenant-a",
            issuer: issuer,
            additionalClaims: new[]
            {
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ANALYST1")
            });

        var realmRegistry = Substitute.For<IIdentityRealmRegistry>();
        var resolverRealm = new IdentityRealm(
            "resolver-realm",
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, FixedTenantId: new TenantId("tenant-a")),
            groupSource: RealmGroupSource.Resolver,
            issuers: new[] { issuer });

        realmRegistry.ResolveRealmByIssuer(issuer).Returns(resolverRealm);
        realmRegistry.GetRealm("resolver-realm").Returns(resolverRealm); // DT-A-3: the tenant resolver looks the stamped realm up by id

        var roleResolver = Substitute.For<IRoleResolver>();
        roleResolver.ResolveRolesAsync(Arg.Any<ResolvedIdentity>())
            .Returns(new RoleResolution(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SecGroup" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SecAnalystRole" },
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1),
                "test"));

        var transformer = new EnterpriseClaimsTransformation(realmRegistry, roleResolver);
        var validator = CreateValidator(claimsTransformation: transformer);

        // Act
        var (isValid, principal) = await validator.ValidateTokenAsync(token);

        // Assert: Principal has resolved role
        isValid.ShouldBeTrue();
        principal.ShouldNotBeNull();
        principal.IsInRole("SecAnalystRole").ShouldBeTrue();
        principal.HasClaim("autheris:group", "SecGroup").ShouldBeTrue();
    }

    [Fact]
    public async Task ConnectionInit_TokenWithoutTenant_UpgradeTenantAcme_Rejected()
    {
        // Arrange: HTTP upgrade was authenticated as Acme tenant
        var httpClaims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-ALICE"),
            new("tenant_id", "acme")
        };
        var httpUser = new ClaimsPrincipal(new ClaimsIdentity(httpClaims, "Cookie"));

        // Token carries no tenant (DT-A-3: no legacy fallback; the socket refuses it as unresolved)
        var tokenClaims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-ALICE")
        };
        var tokenPrincipal = new ClaimsPrincipal(new ClaimsIdentity(tokenClaims, "Bearer"));

        var validator = Substitute.For<ISocketTokenValidator>();
        validator.ValidateTokenAsync("token-legacy", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((true, (ClaimsPrincipal?)tokenPrincipal)));

        var interceptor = new WebSocketAuthInterceptor(
            NullLogger<WebSocketAuthInterceptor>.Instance,
            validator);

        var httpContext = new DefaultHttpContext { User = httpUser };
        var session = Substitute.For<HotChocolate.AspNetCore.Subscriptions.ISocketSession>();
        session.Connection.HttpContext.Returns(httpContext);

        using var doc = System.Text.Json.JsonDocument.Parse("{\"authorization\":\"Bearer token-legacy\"}");
        var payload = Substitute.For<HotChocolate.AspNetCore.Subscriptions.Protocols.IOperationMessagePayload>();
        payload.Payload.Returns(doc.RootElement.Clone());

        // Act
        var result = await interceptor.OnConnectAsync(session, payload);

        // Assert: Strict equality rejects mismatch between LegacySingleTenant and acme
        result.Accepted.ShouldBeFalse();
        result.Message.ShouldBe("Token tenant unresolved");
    }

    [Fact]
    public async Task ConnectionInit_AnonymousUpgrade_TokenWithoutTenantInMultiTenant_Rejected()
    {
        // Arrange: Anonymous HTTP upgrade (no user/tenant on HttpContext)
        var httpContext = new DefaultHttpContext();

        // Token resolves to LegacySingleTenant
        var tokenClaims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-ALICE")
        };
        var tokenPrincipal = new ClaimsPrincipal(new ClaimsIdentity(tokenClaims, "Bearer"));

        var validator = Substitute.For<ISocketTokenValidator>();
        validator.ValidateTokenAsync("token-legacy", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((true, (ClaimsPrincipal?)tokenPrincipal)));

        // Multi-tenant configuration in realm options
        var realmOptions = Options.Create(new IdentityRealmsOptions
        {
            Realms = new Dictionary<string, IdentityRealmConfigOptions>
            {
                ["tenant-realm"] = new()
                {
                    FixedTenantId = "tenant-xyz"
                }
            }
        });

        var interceptor = new WebSocketAuthInterceptor(
            NullLogger<WebSocketAuthInterceptor>.Instance,
            validator,
            realmOptions: realmOptions);

        var session = Substitute.For<HotChocolate.AspNetCore.Subscriptions.ISocketSession>();
        session.Connection.HttpContext.Returns(httpContext);

        using var doc = System.Text.Json.JsonDocument.Parse("{\"authorization\":\"Bearer token-legacy\"}");
        var payload = Substitute.For<HotChocolate.AspNetCore.Subscriptions.Protocols.IOperationMessagePayload>();
        payload.Payload.Returns(doc.RootElement.Clone());

        // Act
        var result = await interceptor.OnConnectAsync(session, payload);

        // Assert: Anonymous upgrade with legacy token in multi-tenant environment rejected
        result.Accepted.ShouldBeFalse();
        result.Message.ShouldBe("Token tenant unresolved");
    }

    [Fact]
    public async Task SocketToken_TransformationThrows_Rejected()
    {
        // Arrange: Claims transformation throws an exception
        var token = GenerateToken("user-broken");

        var transformer = Substitute.For<IClaimsTransformation>();
        transformer.TransformAsync(Arg.Any<ClaimsPrincipal>())
            .Returns<Task<ClaimsPrincipal>>(_ => throw new InvalidOperationException("External identity provider offline"));

        var validator = CreateValidator(claimsTransformation: transformer);

        // Act
        var (isValid, principal) = await validator.ValidateTokenAsync(token);

        // Assert: Fails closed
        isValid.ShouldBeFalse();
        principal.ShouldBeNull();
    }
}
