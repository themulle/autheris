namespace Autheris.Tests.Integration;

using System;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ScimEndpointAuthorizationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ScimSecret = "SecretForScimRealm_12345678901234567890";
    private readonly WebApplicationFactory<Program> _factory;

    public ScimEndpointAuthorizationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<Autheris.Domain.Options.Security.IdentityRealmsOptions>(options =>
                {
                    options.Realms["workforce-entra"] = new Autheris.Domain.Options.Security.IdentityRealmConfigOptions
                    {
                        Provisioning = Autheris.Domain.Identity.RealmProvisioningMode.Scim,
                        Scim = new Autheris.Domain.Options.Security.ScimRealmSecretOptions
                        {
                            PrimarySecret = ScimSecret
                        }
                    };
                });
            });
        });
    }

    [Fact]
    public async Task ScimUsers_ValidRealmSecret_Returns2xxNot500()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ScimSecret);

        var response = await client.GetAsync("/scim/v2/workforce-entra/Users");
        ((int)response.StatusCode).ShouldBeInRange(200, 299, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ScimUsers_NoAuthorization_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/scim/v2/workforce-entra/Users");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ScimUsers_UserJwtOnScimPath_Returns401Or403()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIn0.signature");

        var response = await client.GetAsync("/scim/v2/workforce-entra/Users");
        (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden).ShouldBeTrue();
    }
}
