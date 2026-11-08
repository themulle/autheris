namespace Autheris.Tests.Unit.Security;

using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class AdfsClientAllowlistTests
{
    private const string Authority = "https://adfs.example.org/adfs";

    private static AdfsAuthOptions Options(params string[] clients) => new()
    {
        Enabled = true,
        Authority = Authority,
        Audience = "urn:autheris",
        AllowedClientIds = [.. clients]
    };

    private static ClaimsPrincipal Token(string claimType, string clientId) =>
        new(new ClaimsIdentity([new Claim("sub", "u1"), new Claim(claimType, clientId)], "Bearer"));

    [Theory]
    [InlineData("appid")]
    [InlineData("azp")]
    [InlineData("client_id")]
    public void Listed_client_is_accepted(string claimType) =>
        AdfsTokenPolicy.Apply(Token(claimType, "Client-A"), Options("client-a")).ShouldBeNull();

    [Fact]
    public void Unlisted_client_missing_client_id_and_empty_list_are_rejected()
    {
        AdfsTokenPolicy.Apply(Token("appid", "other"), Options("client-a")).ShouldNotBeNull();
        AdfsTokenPolicy.Apply(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1")], "Bearer")), Options("client-a")).ShouldNotBeNull();
        AdfsTokenPolicy.Apply(Token("appid", "client-a"), Options()).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adfs_token_from_unlisted_client_is_not_authenticated(bool withEntra)
    {
        var entra = new EntraIdAuthOptions { Enabled = withEntra, TenantId = "tenant" };
        var options = new JwtBearerOptions();
        var method = typeof(GatewayServiceCollectionExtensions).GetMethod("ConfigureJwtBearer", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [options, entra, Options("client-a"), Substitute.For<IHostEnvironment>(), false, true]);

        async Task<TokenValidatedContext> Validate(string clientId)
        {
            var http = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddSingleton<IIdentitySubjectResolver>(new IdentitySubjectResolver()).BuildServiceProvider()
            };
            var ctx = new TokenValidatedContext(http, new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)), options)
            {
                Principal = Token("appid", clientId),
                SecurityToken = new JwtSecurityToken(issuer: Authority)
            };
            await options.Events.OnTokenValidated(ctx);
            return ctx;
        }

        (await Validate("client-a")).Result.ShouldBeNull();
        var rejected = await Validate("client-b");
        rejected.Result.ShouldNotBeNull();
        rejected.Result!.Failure.ShouldNotBeNull();
    }
}
