namespace Autheris.Tests.Integration;

using System;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

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
            builder.UseSetting("Gateway:Insecure:warn_fallback_default_tenant_for_webhooks", "true");
            builder.UseSetting("Gateway:Insecure:warn_mock_external_systems_if_unreachable", "true");
            builder.UseSetting("Gateway:Insecure:warn_allow_all_cors_origins", "true");
            builder.UseSetting("Gateway:Insecure:warn_disable_rate_limiting", "true");
            builder.UseSetting("Gateway:Insecure:warn_enable_introspection", "true");

            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:Itsm:Enabled", "true");
        });
    }

    [Fact]
    public async Task Webhook_WithNoSignatureOrOldTimestamp_SucceedsInInsecureMode()
    {
        var client = _factory.CreateClient();

        // 1. Seed a table and a consent request in PENDING_EXTERNAL_APPROVAL
        var ticketId = $"SN-{Guid.NewGuid():N}"[..12];
        var tableId = new TableIdentifier("sales", "crm", "contacts");
        Guid consentRequestId;

        using (var scope = _factory.Services.CreateScope())
        {
            var metaRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
            var approvalRepo = scope.ServiceProvider.GetRequiredService<IConsentApprovalRepository>();

            var table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = tableId.Domain,
                SchemaName = tableId.Schema,
                TableName = tableId.TableName,
                IsActive = true
            };
            var meta = await metaRepo.UpsertTableMetadataAsync(new TableMetadata
            {
                Identifier = tableId,
                Table = table,
                Columns = [new TableColumn { TableId = table.Id, ColumnName = "email", DataType = "VARCHAR" }]
            });

            var req = await approvalRepo.CreateConsentRequestAsync(new ConsentRequest
            {
                TableId = meta.Table.Id,
                TableIdentifier = tableId,
                RequesterSid = new Sid("S-1-5-21-USER1"),
                RequestedGranteeType = GranteeType.User,
                RequestedGranteeRef = "S-1-5-21-USER1",
                BusinessJustification = "Integration test justification",
                RequestedValidTo = DateTimeOffset.UtcNow.AddDays(30),
                Status = "PENDING_EXTERNAL_APPROVAL",
                TenantId = new TenantId("tenant-x")
            });

            await approvalRepo.UpdateConsentRequestTicketIdAsync(req.Id, ticketId);
            consentRequestId = req.Id;
        }

        // 2. Call incoming webhook WITHOUT signature, with 10-hour old timestamp, and unmapped instance
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = ticketId,
            InstanceId = "unknown-instance-dev",
            Action = "APPROVE"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Timestamp", DateTimeOffset.UtcNow.AddHours(-10).ToString("O"));
        // Notice: NO X-ITSM-Signature header!

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 3. Verify consent was activated
        using (var scope = _factory.Services.CreateScope())
        {
            var approvalRepo = scope.ServiceProvider.GetRequiredService<IConsentApprovalRepository>();
            var consentRepo = scope.ServiceProvider.GetRequiredService<IConsentRepository>();

            var updatedReq = await approvalRepo.GetConsentRequestAsync(consentRequestId);
            updatedReq.ShouldNotBeNull();
            updatedReq.Status.ShouldBe("APPROVED");

            var activeConsents = await consentRepo.GetActiveConsentsForSubjectsAsync(
                [new Sid("S-1-5-21-USER1")], tableId, DateTimeOffset.UtcNow);
            activeConsents.Count.ShouldBeGreaterThan(0);
        }
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
    public async Task ExternalItsm_MocksTicket_WhenExternalEndpointIsUnreachable()
    {
        var offlineFactory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Itsm:ServiceNowBaseUrl", "http://127.0.0.1:54321/offline");
        });

        var client = offlineFactory.CreateClient();

        // Seed a table
        var tableId = new TableIdentifier("sales", "crm", "leads");
        using (var scope = offlineFactory.Services.CreateScope())
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
                    tableName: ""leads"",
                    justification: ""Testing mock ITSM fallback"",
                    durationDays: 30
                ) {
                    requestId
                    status
                    message
                    itsmTicketReference {
                        system
                        ticketId
                        ticketUrl
                    }
                }
            }";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql");
        request.Headers.Add("GraphQL-Preflight", "1");
        request.Content = new StringContent(JsonSerializer.Serialize(new { query = mutation }), Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("PENDING_EXTERNAL_APPROVAL");
        content.ShouldContain("MOCK-SERVICENOW-");
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
