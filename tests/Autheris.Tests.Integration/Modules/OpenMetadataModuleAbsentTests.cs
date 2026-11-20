namespace Autheris.Tests.Integration.Modules;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Hosting;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

public sealed class OpenMetadataModuleAbsentTests(OpenMetadataModuleAbsentTests.AbsentHost fixture)
    : IClassFixture<OpenMetadataModuleAbsentTests.AbsentHost>
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task OpenMetadata_webhook_returns_404_without_module_before_body_read()
    {
        using var client = fixture.CreateClient();
        var oversized = new string('a', 2 * 1024 * 1024 + 512);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/openmetadata")
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_config_without_module_fails_fast()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            GovernanceTestDatabase.Apply(builder, "OpenMetadataConfigWithoutModule");
            builder.UseSetting("Gateway:OpenMetadata:WebhookSecret", "configured-without-module");
        });

        var ex = Should.Throw<Exception>(() => factory.CreateClient());
        var text = ex.ToString();
        text.ShouldContain("Gateway:OpenMetadata");
        text.ShouldContain("Autheris.Modules.OpenMetadata");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("OpenMetadata:danger_bypass_webhook_signature_validation", "Insecure:danger_bypass_webhook_signature_validation")]
    [InlineData("OpenMetadata:warn_ignore_webhook_timestamp_tolerance", "Insecure:warn_ignore_webhook_timestamp_tolerance")]
    [InlineData("OpenMetadata:danger_allow_untrusted_certificates", "Insecure:danger_allow_untrusted_certificates")]
    public void Legacy_insecure_switches_fail_fast_without_module(string legacyKey, string expectedReplacement)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            GovernanceTestDatabase.Apply(builder, "OpenMetadataLegacySwitchWithoutModule");
            builder.UseSetting("Gateway:" + legacyKey, "true");
        });

        var ex = Should.Throw<Exception>(() => factory.CreateClient());
        var invEx = Unwrap<InvalidOperationException>(ex);
        invEx.ShouldNotBeNull();
        invEx.Message.ShouldContain("Gateway:" + expectedReplacement);
    }

    [Fact]
    public async Task Sync_mutation_returns_unavailable_without_module()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-CLUSTER-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "ClusterAdmin");

        var omResponse = await client.PostAsync(
            "/graphql",
            new StringContent(
                JsonSerializer.Serialize(new { query = "mutation { syncOpenMetadata(dryRun: true) { success } }" }),
                Encoding.UTF8,
                "application/json"));
        var omBody = await omResponse.Content.ReadAsStringAsync();

        omResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        omBody.ShouldContain("UNAVAILABLE");
        omBody.ShouldContain("OpenMetadata is not available.");

        var catResponse = await client.PostAsync(
            "/graphql",
            new StringContent(
                JsonSerializer.Serialize(new { query = "mutation { syncDataCatalog(dryRun: true) { success } }" }),
                Encoding.UTF8,
                "application/json"));
        var catBody = await catResponse.Content.ReadAsStringAsync();

        catResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        catBody.ShouldContain("UNAVAILABLE");
        catBody.ShouldContain("The data catalog is not available.");
    }

    [Fact]
    public async Task GraphQL_queries_work_without_openmetadata_module()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-CLUSTER-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "ClusterAdmin");

        var response = await client.PostAsync(
            "/graphql",
            new StringContent(
                JsonSerializer.Serialize(new { query = "{ __schema { queryType { name } } }" }),
                Encoding.UTF8,
                "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotContain("\"errors\"");
        body.ShouldContain("\"queryType\"");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Catalog_enabled_without_module_fails_fast()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            GovernanceTestDatabase.Apply(builder, "CatalogEnabledWithoutModule");
            builder.UseSetting("Gateway:Catalog:Enabled", "true");
        });

        var ex = Should.Throw<Exception>(() => factory.CreateClient());
        var loadEx = Unwrap<ModuleLoadException>(ex);
        loadEx.ShouldNotBeNull();
        loadEx.ModuleId.ShouldBe("host");
        loadEx.Message.ShouldContain("openmetadata");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Non_admin_sync_without_module_returns_forbidden()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-NON-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "StandardUser");

        var response = await client.PostAsync(
            "/graphql",
            new StringContent(
                JsonSerializer.Serialize(new { query = "mutation { syncOpenMetadata(dryRun: true) { success } }" }),
                Encoding.UTF8,
                "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("FORBIDDEN");
        body.ShouldNotContain("UNAVAILABLE");
    }

    private static TException? Unwrap<TException>(Exception ex) where TException : Exception
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is TException typed)
            {
                return typed;
            }
        }

        return null;
    }

    public sealed class AbsentHost : IntegrationHost<AbsentHost>
    {
        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("OpenMetadataModuleAbsent"),
            });
        }
    }
}
