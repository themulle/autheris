namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class RealmTokenScopePolicyTests
{
    private const string MappedScopeClaimType = "http://schemas.microsoft.com/identity/claims/scope";

    private static IdentityRealmConfigOptions DefaultRealm(List<string>? readOnlyScopes = null) => new()
    {
        Kind = IdentityRealmKind.GenericOidc,
        Issuers = ["https://issuer.example"],
        Audiences = ["autheris"],
        UserKeyClaim = "sub",
        ReadOnlyScopes = readOnlyScopes
    };

    private static ClaimsPrincipal Token(string? scopeValue, string claimType = "scope")
    {
        var claims = new List<Claim> { new("sub", "system:serviceaccount:data:dbt-sync") };
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

        RealmTokenScopePolicy.Apply(principal, DefaultRealm());

        principal.IsReadOnly().ShouldBeTrue();
    }

    [Fact]
    public void ScopeClaimRfc9068_IsRead()
    {
        var principal = Token("Agent.Read", "scope");

        RealmTokenScopePolicy.Apply(principal, DefaultRealm());

        principal.IsReadOnly().ShouldBeTrue();
    }

    [Theory]
    [InlineData("Agent.Read Data.Write", "scope")]
    [InlineData("Agent.Read Sql.Write", "scope")]
    [InlineData("Sql.Write", "scope")]
    [InlineData("Agent.Read Data.Write", "scp")]
    public void MixedReadAndWriteScopes_NotMarked(string scopeValue, string claimType)
    {
        var principal = Token(scopeValue, claimType);

        RealmTokenScopePolicy.Apply(principal, DefaultRealm());

        principal.IsReadOnly().ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoScopeClaim_NotMarked(string? scopeValue)
    {
        var principal = Token(scopeValue, "scope");

        RealmTokenScopePolicy.Apply(principal, DefaultRealm());

        principal.IsReadOnly().ShouldBeFalse();
    }

    [Fact]
    public void EmptyReadOnlyList_NotMarked()
    {
        var principal = Token("Agent.Read", "scope");
        var realm = DefaultRealm([]);

        RealmTokenScopePolicy.Apply(principal, realm);

        principal.IsReadOnly().ShouldBeFalse();
    }

    [Theory]
    [InlineData("Custom.Read")]
    [InlineData("https://issuer.example/Custom.Read")]
    public void CustomReadOnlyScopes_FullUriForm_IsMarkedReadOnly(string tokenScope)
    {
        var principal = Token(tokenScope, "scope");
        var realm = DefaultRealm(["api://autheris/Custom.Read"]);

        RealmTokenScopePolicy.Apply(principal, realm);

        principal.IsReadOnly().ShouldBeTrue();
    }

    [Fact]
    public void NullPrincipalOrRealm_DoesNotThrow()
    {
        Should.NotThrow(() => RealmTokenScopePolicy.Apply(null, DefaultRealm()));
        Should.NotThrow(() => RealmTokenScopePolicy.Apply(Token("Agent.Read"), null));
    }

    [Fact]
    public async Task RealmScheme_RegistersOnTokenValidated()
    {
        var options = new JwtBearerOptions();
        var realm = DefaultRealm();

        GatewayServiceCollectionExtensions.ConfigureGenericOidcJwtBearer(
            options, realm, staticKeys: null, new FakeHostEnvironment("Production"));

        options.Events.ShouldNotBeNull();
        options.Events.OnTokenValidated.ShouldNotBeNull();

        var scheme = new AuthenticationScheme("Bearer:realm:wl", null, typeof(JwtBearerHandler));

        var readOnlyPrincipal = Token("Agent.Read", "scope");
        var readOnlyCtx = new TokenValidatedContext(new DefaultHttpContext(), scheme, options)
        {
            Principal = readOnlyPrincipal,
            SecurityToken = new JwtSecurityToken(issuer: "https://issuer.example")
        };
        await options.Events.OnTokenValidated(readOnlyCtx);
        readOnlyPrincipal.IsReadOnly().ShouldBeTrue();

        var writablePrincipal = Token("Agent.Read Sql.Write", "scope");
        var writableCtx = new TokenValidatedContext(new DefaultHttpContext(), scheme, options)
        {
            Principal = writablePrincipal,
            SecurityToken = new JwtSecurityToken(issuer: "https://issuer.example")
        };
        await options.Events.OnTokenValidated(writableCtx);
        writablePrincipal.IsReadOnly().ShouldBeFalse();
    }
}
