namespace Autheris.Tests.Integration;

using System;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Autheris.Tests.Common.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Autheris.Tests.Integration.Governance;

[Trait("Category", "Security")]
public sealed class ScimEndpointAuthorizationTests : IClassFixture<ScimEndpointAuthorizationTests.HostFixture>
{
    private const string ScimSecret = "SecretForScimRealm_12345678901234567890";
    private readonly WebApplicationFactory<Program> _factory;

    public ScimEndpointAuthorizationTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("ScimEndpointAuthorizationTests"));
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
        }
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

    [Fact]
    public async Task Membership_change_effective_next_request()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ScimSecret);

        var userPayload = System.Net.Http.Json.JsonContent.Create(new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "nextreq-user@corp.example",
            externalId = "nextreq-oid-1",
            active = true
        });
        var createUserResp = await client.PostAsync("/scim/v2/workforce-entra/Users", userPayload);
        ((int)createUserResp.StatusCode).ShouldBeInRange(200, 299, await createUserResp.Content.ReadAsStringAsync());
        using var userDoc = System.Text.Json.JsonDocument.Parse(await createUserResp.Content.ReadAsStringAsync());
        var userId = userDoc.RootElement.GetProperty("id").GetString()!;

        const string groupSid = "S-1-5-21-100-200-300-8801";
        var groupPayload = System.Net.Http.Json.JsonContent.Create(new
        {
            schemas = new[]
            {
                "urn:ietf:params:scim:schemas:core:2.0:Group",
                Autheris.Domain.Identity.ScimSchemaUris.AutherisGroupExtension
            },
            displayName = "NextRequest Group",
            externalId = "nextreq-grp-1",
            members = new[] { new { value = userId, type = "User" } },
            objectSid = groupSid
        });
        var createGroupResp = await client.PostAsync("/scim/v2/workforce-entra/Groups", groupPayload);
        ((int)createGroupResp.StatusCode).ShouldBeInRange(200, 299, await createGroupResp.Content.ReadAsStringAsync());
        using var groupDoc = System.Text.Json.JsonDocument.Parse(await createGroupResp.Content.ReadAsStringAsync());
        var groupId = groupDoc.RootElement.GetProperty("id").GetString()!;

        using (var scope = _factory.Services.CreateScope())
        {
            var identityRepo = scope.ServiceProvider.GetRequiredService<Autheris.Domain.Interfaces.IIdentityStoreRepository>();
            var before = await identityRepo.GetEffectiveGroupSidsForUserAsync(
                new Autheris.Domain.Common.TenantId("default"), "workforce-entra", userId);
            before.GroupObjectSids.ShouldContain(groupSid);

            var consentCache = scope.ServiceProvider.GetRequiredService<Autheris.Application.Common.Interfaces.IConsentCacheService>();
            var cachedTable = new Autheris.Domain.Common.TableIdentifier("default", "public", "scim_next_req");
            await consentCache.SetCachedDecisionAsync(
                new Autheris.Domain.Common.TenantId("default"),
                new Autheris.Domain.Common.Sid(userId),
                cachedTable,
                Autheris.Domain.Interfaces.TableAccessDecision.Allowed(
                    cachedTable,
                    new System.Collections.Generic.Dictionary<string, Autheris.Domain.Interfaces.ColumnAccessLevel>(),
                    hasUnconstrainedColumnAllow: true),
                TimeSpan.FromMinutes(5));
            (await consentCache.GetCachedDecisionAsync(
                new Autheris.Domain.Common.TenantId("default"),
                new Autheris.Domain.Common.Sid(userId),
                cachedTable)).ShouldNotBeNull();
        }

        var patchPayload = System.Net.Http.Json.JsonContent.Create(new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[]
            {
                new
                {
                    op = "remove",
                    path = $"members[value eq \"{userId}\"]"
                }
            }
        });
        var patchResp = await client.PatchAsync($"/scim/v2/workforce-entra/Groups/{groupId}", patchPayload);
        ((int)patchResp.StatusCode).ShouldBeInRange(200, 299, await patchResp.Content.ReadAsStringAsync());

        using (var verifyScope = _factory.Services.CreateScope())
        {
            var identityRepo = verifyScope.ServiceProvider.GetRequiredService<Autheris.Domain.Interfaces.IIdentityStoreRepository>();
            var after = await identityRepo.GetEffectiveGroupSidsForUserAsync(
                new Autheris.Domain.Common.TenantId("default"), "workforce-entra", userId);
            after.GroupObjectSids.ShouldNotContain(groupSid);
        }
    }
}
