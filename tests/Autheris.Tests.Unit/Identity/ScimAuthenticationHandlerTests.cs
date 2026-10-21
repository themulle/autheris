namespace Autheris.Tests.Unit.Identity;

using System;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ScimAuthenticationHandlerTests
{
    private const string ExpectedSecret = "SuperSecretScimKey1234567890";
    private readonly IOptionsMonitor<AuthenticationSchemeOptions> _optionsMonitor;
    private readonly IOptionsMonitor<IdentityRealmsOptions> _realmOptionsMonitor;
    private readonly IdentityRealmsOptions _realmOptions;

    public ScimAuthenticationHandlerTests()
    {
        _optionsMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        _optionsMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        _realmOptions = new IdentityRealmsOptions();
        _realmOptions.Scim.PrimarySecret = ExpectedSecret;

        _realmOptionsMonitor = Substitute.For<IOptionsMonitor<IdentityRealmsOptions>>();
        _realmOptionsMonitor.CurrentValue.Returns(_realmOptions);
    }

    private async Task<AuthenticateResult> RunAuthenticateAsync(string? authHeader, string path = "/scim/v2/workforce-entra/Users")
    {
        var handler = new ScimAuthenticationHandler(
            _optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            _realmOptionsMonitor);

        var context = new DefaultHttpContext();
        if (authHeader != null)
        {
            context.Request.Headers["Authorization"] = authHeader;
        }
        context.Request.Path = path;

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.Scim, GatewayAuthSchemes.Scim, typeof(ScimAuthenticationHandler)), context);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task ScimAuthenticationHandler_ValidSecret_AuthenticatesConstantTime()
    {
        var result = await RunAuthenticateAsync($"Bearer {ExpectedSecret}");

        result.Succeeded.ShouldBeTrue();
        result.Principal.ShouldNotBeNull();
        result.Principal.Identity?.Name.ShouldBe("scim-client:workforce-entra");
        result.Principal.HasClaim("autheris:realm", "workforce-entra").ShouldBeTrue();
        result.Principal.IsInRole("SCIM_PROVISIONER").ShouldBeTrue();
    }

    [Fact]
    public async Task ScimAuthenticationHandler_ValidSecret_IsNeverClusterAdmin()
    {
        var result = await RunAuthenticateAsync($"Bearer {ExpectedSecret}");

        result.Succeeded.ShouldBeTrue();
        result.Principal!.IsInRole("ClusterAdmin").ShouldBeFalse();
        result.Principal.IsInRole(GatewayPolicies.ScimProvisionerRole).ShouldBeTrue();
        GatewayPolicies.IsScimProvisioner(result.Principal).ShouldBeTrue();
    }

    [Fact]
    public async Task ScimAuthenticationHandler_RealmsConfigured_RejectsRealmWithoutScimProvisioning()
    {
        _realmOptions.Realms["workforce-entra"] = new IdentityRealmConfigOptions { Provisioning = RealmProvisioningMode.Scim };
        _realmOptions.Realms["partners"] = new IdentityRealmConfigOptions { Provisioning = RealmProvisioningMode.Jit };

        (await RunAuthenticateAsync($"Bearer {ExpectedSecret}", "/scim/v2/workforce-entra/Users")).Succeeded.ShouldBeTrue();
        (await RunAuthenticateAsync($"Bearer {ExpectedSecret}", "/scim/v2/partners/Users")).Succeeded.ShouldBeFalse();
        (await RunAuthenticateAsync($"Bearer {ExpectedSecret}", "/scim/v2/unknown-realm/Groups")).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task ScimAuthenticationHandler_InvalidSecret_Rejects()
    {
        var result = await RunAuthenticateAsync("Bearer WrongSecretTotallyInvalid");

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task ScimAuthenticationHandler_StandardUserToken_Rejected()
    {
        // Simulated standard Entra ID JWT bearer token presented to SCIM endpoint
        var standardUserJwt = "Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIn0.signature";
        var result = await RunAuthenticateAsync(standardUserJwt);

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }
}
