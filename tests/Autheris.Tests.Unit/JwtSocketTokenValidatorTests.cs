namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class JwtSocketTokenValidatorTests
{
    private readonly SymmetricSecurityKey _signingKey = new(Encoding.UTF8.GetBytes("a-very-secret-test-key-of-at-least-256-bits-length!"));

    private (JwtSocketTokenValidator Validator, IHostEnvironment Env) CreateValidator(bool isDev = false, bool enableTestAuth = false)
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

        var gatewayOptions = Options.Create(new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                EnableTestAuthHandler = enableTestAuth
            }
        });

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(isDev ? Environments.Development : Environments.Production);

        var validator = new JwtSocketTokenValidator(
            jwtOptionsMonitor,
            gatewayOptions,
            env,
            NullLogger<JwtSocketTokenValidator>.Instance);

        return (validator, env);
    }

    private string GenerateToken(string sub, SecurityKey? key = null)
    {
        var keyToUse = key ?? _signingKey;
        var tokenHandler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, sub),
                new Claim("sub", sub),
                new Claim("tenant_id", "tenant-test")
            ]),
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

        var jwtOptionsMonitor = Substitute.For<IOptionsMonitor<JwtBearerOptions>>();
        jwtOptionsMonitor.Get(Arg.Any<string>()).Returns(jwtOptions);

        var gatewayOptions = Options.Create(new GatewayOptions());
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);

        var validator = new JwtSocketTokenValidator(
            jwtOptionsMonitor,
            gatewayOptions,
            env,
            NullLogger<JwtSocketTokenValidator>.Instance);

        var token = GenerateToken("user-bob");

        var (isValid, principal) = await validator.ValidateTokenAsync(token);

        isValid.ShouldBeFalse();
        principal.ShouldBeNull();
    }
}
