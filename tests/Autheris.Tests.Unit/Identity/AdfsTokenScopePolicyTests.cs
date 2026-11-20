namespace Autheris.Tests.Unit.Identity;

using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class AdfsTokenScopePolicyTests
{
    private const string Authority = "https://adfs.example.org/adfs";
    private const string MappedScopeClaimType = "http://schemas.microsoft.com/identity/claims/scope";

    private static AdfsAuthOptions DefaultAdfsOptions(params string[] clients) => new()
    {
        Enabled = true,
        Authority = Authority,
        Audience = "urn:autheris",
        AllowedClientIds = clients.Length > 0 ? [.. clients] : ["adfs-client"]
    };

    private static ClaimsPrincipal Token(string? scopeValue, string claimType = "scope", string clientId = "adfs-client")
    {
        var claims = new List<Claim>
        {
            new("sub", "u1"),
            new("appid", clientId)
        };
        if (scopeValue is not null)
        {
            claims.Add(new Claim(claimType, scopeValue));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    [Theory]
    [InlineData("Agent.Read", "scp")]
    [InlineData("App.Read", "scp")]
    [InlineData("Agent.Read", MappedScopeClaimType)]
    [InlineData("agent.read", "scope")]
    [InlineData("Agent.Read App.Read", "scope")]
    public void ReadScopeOnly_IsMarkedReadOnly(string scopeValue, string claimType)
    {
        var principal = Token(scopeValue, claimType);

        AdfsTokenPolicy.Apply(principal, DefaultAdfsOptions()).ShouldBeNull();

        principal.IsReadOnly().ShouldBeTrue();
    }

    [Theory]
    [InlineData("Agent.Read Data.Write", "scope")]
    [InlineData("Agent.Read Sql.Write", "scope")]
    [InlineData("Sql.Write", "scope")]
    [InlineData("Agent.Read Data.Write", "scp")]
    [InlineData("Agent.Read Sql.Write", MappedScopeClaimType)]
    public void MixedReadAndWriteScopes_NotMarked(string scopeValue, string claimType)
    {
        var principal = Token(scopeValue, claimType);

        AdfsTokenPolicy.Apply(principal, DefaultAdfsOptions()).ShouldBeNull();

        principal.IsReadOnly().ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoScopeClaim_NotMarked(string? scopeValue)
    {
        var principal = Token(scopeValue, "scope");

        AdfsTokenPolicy.Apply(principal, DefaultAdfsOptions()).ShouldBeNull();

        principal.IsReadOnly().ShouldBeFalse();
    }

    [Fact]
    public void UnlistedClient_WithReadOnlyScope_IsRejectedAndNotMarked()
    {
        var principal = Token("Agent.Read", "scope", clientId: "unlisted-client");

        AdfsTokenPolicy.Apply(principal, DefaultAdfsOptions("adfs-client")).ShouldNotBeNull();

        principal.IsReadOnly().ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdfsScheme_OnTokenValidated_HonoursReadOnlyScopes(bool withEntra)
    {
        var entra = new EntraIdAuthOptions { Enabled = withEntra, TenantId = "tenant" };
        var adfs = DefaultAdfsOptions();
        var options = new JwtBearerOptions();
        var method = typeof(GatewayServiceCollectionExtensions).GetMethod("ConfigureJwtBearer", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [options, entra, adfs, new FakeHostEnvironment("Production"), false, true]);

        options.Events.ShouldNotBeNull();
        options.Events.OnTokenValidated.ShouldNotBeNull();

        var scheme = new AuthenticationScheme(GatewayAuthSchemes.JwtBearerAdfs, null, typeof(JwtBearerHandler));

        async Task<ClaimsPrincipal> ValidateAsync(string scopeValue, string claimType)
        {
            var principal = Token(scopeValue, claimType);
            var http = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddSingleton<IIdentitySubjectResolver>(new IdentitySubjectResolver()).BuildServiceProvider()
            };
            var ctx = new TokenValidatedContext(http, scheme, options)
            {
                Principal = principal,
                SecurityToken = new JwtSecurityToken(issuer: Authority)
            };
            await options.Events.OnTokenValidated(ctx);
            ctx.Result.ShouldBeNull();
            return principal;
        }

        (await ValidateAsync("Agent.Read", "scope")).IsReadOnly().ShouldBeTrue();
        (await ValidateAsync("App.Read", "scp")).IsReadOnly().ShouldBeTrue();
        (await ValidateAsync("Agent.Read", MappedScopeClaimType)).IsReadOnly().ShouldBeTrue();
        (await ValidateAsync("Agent.Read Sql.Write", "scope")).IsReadOnly().ShouldBeFalse();
    }

    [Fact]
    public void TokenPolicySelector_AdfsIssuer_HonoursReadOnlyScopes()
    {
        var auth = new Autheris.Domain.Options.AuthenticationOptions
        {
            Adfs = DefaultAdfsOptions()
        };
        var resolver = new IdentitySubjectResolver();

        var readOnlyPrincipal = Token("Agent.Read", "scope");
        TokenPolicySelector.Apply(readOnlyPrincipal, Authority, auth, resolver).ShouldBeNull();
        readOnlyPrincipal.IsReadOnly().ShouldBeTrue();

        var mixedPrincipal = Token("Agent.Read Sql.Write", "scope");
        TokenPolicySelector.Apply(mixedPrincipal, Authority, auth, resolver).ShouldBeNull();
        mixedPrincipal.IsReadOnly().ShouldBeFalse();
    }
}
