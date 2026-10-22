namespace Autheris.Tests.Unit.Security;

using System.Security.Claims;
using Autheris.Api.Security;
using Autheris.Application.Services;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

public sealed class EntraTokenPolicyAccessTokenShapeTests
{
    private static readonly IdentitySubjectResolver Resolver = new();

    [Fact]
    public void Apply_IdTokenWithoutNonceWithRoles_Rejected()
    {
        // An Entra ID token that carries application roles but no nonce and no azp/appid
        var idToken = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "11111111-2222-3333-4444-555555555555"),
                new Claim("preferred_username", "alice@corp.example"),
                new Claim("roles", "Reader")
            ],
            "Bearer"));

        var error = EntraTokenPolicy.Apply(idToken, new EntraIdAuthOptions(), Resolver);
        error.ShouldNotBeNull();
        error.ShouldContain("missing 'azp'/'appid'");
    }

    [Fact]
    public void Apply_TokenWithoutAzpAndAppid_Rejected()
    {
        var token = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "11111111-2222-3333-4444-555555555555"),
                new Claim("scp", "user_impersonation")
            ],
            "Bearer"));

        var error = EntraTokenPolicy.Apply(token, new EntraIdAuthOptions(), Resolver);
        error.ShouldNotBeNull();
        error.ShouldContain("missing 'azp'/'appid'");
    }

    [Fact]
    public void Apply_V1AccessTokenWithAppid_Accepted()
    {
        var v1Token = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "11111111-2222-3333-4444-555555555555"),
                new Claim("appid", "client-app-v1"),
                new Claim("scp", "user_impersonation"),
                new Claim("preferred_username", "bob@corp.example")
            ],
            "Bearer"));

        var error = EntraTokenPolicy.Apply(v1Token, new EntraIdAuthOptions(), Resolver);
        error.ShouldBeNull();
    }

    [Fact]
    public void Apply_V2AccessTokenWithAzp_Accepted()
    {
        var v2Token = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "11111111-2222-3333-4444-555555555555"),
                new Claim("azp", "client-app-v2"),
                new Claim("scp", "user_impersonation"),
                new Claim("preferred_username", "carol@corp.example")
            ],
            "Bearer"));

        var error = EntraTokenPolicy.Apply(v2Token, new EntraIdAuthOptions(), Resolver);
        error.ShouldBeNull();
    }

    [Fact]
    public void Apply_DelegatedTokenWithRolesButNoScp_Rejected()
    {
        var userToken = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "11111111-2222-3333-4444-555555555555"),
                new Claim("azp", "client-app-v2"),
                new Claim("preferred_username", "david@corp.example"),
                new Claim("roles", "Reader")
            ],
            "Bearer"));

        var error = EntraTokenPolicy.Apply(userToken, new EntraIdAuthOptions(), Resolver);
        error.ShouldNotBeNull();
        error.ShouldContain("Delegated tokens require 'scp'");
    }

    [Fact]
    public void Apply_IdtypUnknown_Rejected()
    {
        var invalidIdtypToken = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "11111111-2222-3333-4444-555555555555"),
                new Claim("azp", "client-app-v2"),
                new Claim("idtyp", "service"), // Invalid idtyp (must be app or user)
                new Claim("scp", "user_impersonation"),
                new Claim("preferred_username", "eve@corp.example")
            ],
            "Bearer"));

        var error = EntraTokenPolicy.Apply(invalidIdtypToken, new EntraIdAuthOptions(), Resolver);
        error.ShouldNotBeNull();
        error.ShouldContain("invalid 'idtyp' claim");
    }

    [Fact]
    public void Apply_AppOnlyTokenWithAzpAndRoles_StillFollowsAppOnlyPolicy()
    {
        var appOnlyToken = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "22222222-3333-4444-5555-666666666666"),
                new Claim("azp", "daemon-app"),
                new Claim("idtyp", "app"),
                new Claim("roles", "Reader")
            ],
            "Bearer"));

        var options = new EntraIdAuthOptions { AppOnlyTokens = AppOnlyTokenAccess.ReadOnly };
        var error = EntraTokenPolicy.Apply(appOnlyToken, options, Resolver);
        error.ShouldBeNull();
        appOnlyToken.IsReadOnly().ShouldBeTrue();
    }
}
