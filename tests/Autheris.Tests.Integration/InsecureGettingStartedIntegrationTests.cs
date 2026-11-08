namespace Autheris.Tests.Integration;

using System;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Autheris.Tests.Integration.Governance;

public class InsecureGettingStartedIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public InsecureGettingStartedIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            builder.UseSetting("Gateway:Insecure:danger_disable_column_masking", "true");
            builder.UseSetting("Gateway:Insecure:danger_bypass_webhook_signature_validation", "true");
            builder.UseSetting("Gateway:Insecure:danger_allow_untrusted_certificates", "true");
            builder.UseSetting("Gateway:Insecure:warn_ignore_webhook_timestamp_tolerance", "true");
            builder.UseSetting("Gateway:Insecure:warn_mock_external_systems_if_unreachable", "true");
            builder.UseSetting("Gateway:Insecure:warn_allow_all_cors_origins", "true");
            builder.UseSetting("Gateway:Insecure:warn_disable_rate_limiting", "true");
            builder.UseSetting("Gateway:Insecure:warn_enable_introspection", "true");

            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("InsecureGettingStartedIntegrationTests"));
        });
    }

    [Fact]
    public async Task HealthEndpoint_And_ResponseHeaders_ReportInsecureMode()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Response header must warn developers
        response.Headers.Contains("X-Gateway-Insecure-Mode").ShouldBeTrue();
        response.Headers.GetValues("X-Gateway-Insecure-Mode").First().ShouldContain("DANGER:");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("securityMode").GetString().ShouldBe("INSECURE_DEV_MODE");
        json.GetProperty("activeBypasses").GetArrayLength().ShouldBeGreaterThan(5);
    }

    [Fact]
    public async Task AutoApprove_ImmediatelyApprovesConsentRequest()
    {
        var autoApproveFactory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Insecure:warn_auto_approve_access_requests", "true");
        });

        var client = autoApproveFactory.CreateClient();

        var tableId = new TableIdentifier("sales", "crm", "customers");
        using (var scope = autoApproveFactory.Services.CreateScope())
        {
            var metaRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
            var table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = tableId.Domain,
                SchemaName = tableId.Schema,
                TableName = tableId.TableName,
                IsActive = true
            };
            await metaRepo.UpsertTableMetadataAsync(new TableMetadata
            {
                Identifier = tableId,
                Table = table,
                Columns = [new TableColumn { TableId = table.Id, ColumnName = "name", DataType = "VARCHAR" }]
            });
        }

        var mutation = @"
            mutation {
                requestTableAccess(
                    domain: ""sales"",
                    schema: ""crm"",
                    tableName: ""customers"",
                    justification: ""Testing auto-approval"",
                    durationDays: 14
                ) {
                    requestId
                    status
                    message
                }
            }";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql");
        request.Headers.Add("GraphQL-Preflight", "1");
        request.Content = new StringContent(JsonSerializer.Serialize(new { query = mutation }), Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("APPROVED");
        content.ShouldContain("auto-approved");
    }
}
