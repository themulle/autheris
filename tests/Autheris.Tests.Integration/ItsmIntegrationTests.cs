namespace Autheris.Tests.Integration;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

public class ItsmIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private const string WebhookSecret = "super-secret-itsm-token-12345";

    // SEC H-06: Webhooks are verified with a secret per ITSM instance; the global secret above is no longer accepted.
    private const string InstanceSecretA = "itsm-instance-secret-tenant-a-0001";
    private const string InstanceSecretB = "itsm-instance-secret-tenant-b-0002";

    public ItsmIntegrationTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("ITSM__WEBHOOK_SECRET", WebhookSecret);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "500");
            builder.UseSetting("Gateway:Itsm:Enabled", "true");
            builder.UseSetting("Gateway:Itsm:InstanceToTenantMap:inst-tenant-a", "tenant-a");
            builder.UseSetting("Gateway:Itsm:InstanceToTenantMap:inst-tenant-b", "tenant-b");
            builder.UseSetting("itsm:webhook-secret:inst-tenant-a", InstanceSecretA);
            builder.UseSetting("itsm:webhook-secret:inst-tenant-b", InstanceSecretB);
        });
    }

    private static string ComputeSignature(string payload, DateTimeOffset? timestamp = null, string secret = InstanceSecretA)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var message = $"t={ts:O}.v1={payload}";
        var hash = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash);
    }

    [Fact]
    public async Task Webhook_WithInvalidSignature_Returns401Unauthorized()
    {
        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = "INC1001",
            InstanceId = "inst-tenant-a",
            Action = "APPROVE"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", "DEADBEEF01020304");
        request.Headers.Add("X-ITSM-Timestamp", DateTimeOffset.UtcNow.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_WithExpiredTimestamp_Returns401Unauthorized()
    {
        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = "INC1002",
            InstanceId = "inst-tenant-a",
            Action = "APPROVE"
        });

        var expiredTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
        var signature = ComputeSignature(payload, expiredTimestamp);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", signature);
        request.Headers.Add("X-ITSM-Timestamp", expiredTimestamp.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_ReplayAttackWithModifiedTimestamp_Returns401Unauthorized()
    {
        // Finding B3 Verification:
        // An attacker has a legitimate payload and signature signed at T1.
        // Attacker attempts to replay by changing the timestamp header to T2 (fresh).
        // Since the HMAC strictly binds the timestamp, this must be rejected with 401 Unauthorized.
        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = "INC9999",
            InstanceId = "inst-tenant-a",
            Action = "APPROVE"
        });

        var t1 = DateTimeOffset.UtcNow.AddMinutes(-2);
        var validSignatureAtT1 = ComputeSignature(payload, t1);

        var t2 = DateTimeOffset.UtcNow; // Fresh timestamp
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", validSignatureAtT1);
        request.Headers.Add("X-ITSM-Timestamp", t2.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_WithCrossTenantMismatch_RejectsAndDoesNotActivateConsent()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        // Create consent request for tenant-a
        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"TICKET-{Guid.NewGuid():N}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-USER-1"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-USER-1",
            BusinessJustification = "Cross-tenant mismatch test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        // Webhook callback comes from instance bound to tenant-b!
        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = ticketId,
            InstanceId = "inst-tenant-b", // Maps to tenant-b -> mismatch!
            Action = "APPROVE"
        });

        var timestamp = DateTimeOffset.UtcNow;
        // SEC H-06: Correctly signed by instance B (its own secret) - the tenant-bound lookup must still not find tenant A's ticket.
        var signature = ComputeSignature(payload, timestamp, InstanceSecretB);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", signature);
        request.Headers.Add("X-ITSM-Timestamp", timestamp.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Verify request is STILL in PENDING_EXTERNAL_APPROVAL (not activated)
        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
    }

    [Fact]
    public async Task Webhook_WithValidSignatureAndTenant_ActivatesConsentIdempotently()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"TICKET-{Guid.NewGuid():N}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-USER-2"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-USER-2",
            BusinessJustification = "Valid webhook test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = ticketId,
            InstanceId = "inst-tenant-a", // Maps to tenant-a -> matches!
            Action = "APPROVE",
            Approver = "dataowner@corp.local" // review R3-1/R2-5: owned tables need a named owner/delegate
        });

        var timestamp = DateTimeOffset.UtcNow;
        var signature = ComputeSignature(payload, timestamp);
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change"))
        {
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            request.Headers.Add("X-ITSM-Signature", signature);
            request.Headers.Add("X-ITSM-Timestamp", timestamp.ToString("O"));

            var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Verify request is now APPROVED
        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("APPROVED");

        // Verify active consent was created
        var activeConsents = await repo.GetActiveConsentsForSubjectsAsync(
            [new Sid("S-1-5-21-USER-2")],
            tableId,
            DateTimeOffset.UtcNow);
        activeConsents.Count.ShouldBeGreaterThan(0);

        // Repeat webhook call (idempotency check)
        using (var repeatRequest = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change"))
        {
            repeatRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            repeatRequest.Headers.Add("X-ITSM-Signature", signature);
            repeatRequest.Headers.Add("X-ITSM-Timestamp", timestamp.ToString("O"));

            var repeatResponse = await client.SendAsync(repeatRequest);
            repeatResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Webhook_WithoutTimestampHeader_Returns400BadRequest()
    {
        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = "INC1001",
            InstanceId = "inst-tenant-a",
            Action = "APPROVE"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", "DEADBEEF01020304");
        // No X-ITSM-Timestamp header!

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Webhook_WhenActionIsReject_RejectsConsentRequest()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"REJECT-TICKET-{Guid.NewGuid():N}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-REJECT-USER"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-REJECT-USER",
            BusinessJustification = "Reject webhook test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = ticketId,
            InstanceId = "inst-tenant-a",
            Action = "REJECT",
            Reason = "Denied by governance review in ServiceNow"
        });

        var timestamp = DateTimeOffset.UtcNow;
        var signature = ComputeSignature(payload, timestamp);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", signature);
        request.Headers.Add("X-ITSM-Timestamp", timestamp.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Verify request is REJECTED
        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("REJECTED");

        // Verify NO active consent was created
        var activeConsents = await repo.GetActiveConsentsForSubjectsAsync(
            [new Sid("S-1-5-21-REJECT-USER")],
            tableId,
            DateTimeOffset.UtcNow);
        activeConsents.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Webhook_WithTimestampBoundSignature_ActivatesConsentSuccessfully()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"TS-BOUND-TICKET-{Guid.NewGuid():N}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-TS-USER"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-TS-USER",
            BusinessJustification = "Timestamp-bound signature test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = ticketId,
            InstanceId = "inst-tenant-a",
            Action = "APPROVE",
            Approver = "dataowner@corp.local" // review R3-1/R2-5: owned tables need a named owner/delegate
        });

        var timestamp = DateTimeOffset.UtcNow;
        var signature = ComputeSignature(payload, timestamp); // Signs $"t={timestamp:O}.v1={payload}"
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", signature);
        request.Headers.Add("X-ITSM-Timestamp", timestamp.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Verify request is APPROVED
        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("APPROVED");
    }

    [Fact]
    public async Task Mutation_RequestTableAccess_WhenItsmFails_RollsBackRequestLeavingZeroOrphanedRecords()
    {
        var offlineFactory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Itsm:ServiceNowBaseUrl", "http://127.0.0.1:54321/offline");
        });

        var client = offlineFactory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-a");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER-1");

        var uniqueJustification = $"Rollback-Test-{Guid.NewGuid():N}";
        var mutation = $$"""
            mutation {
                requestTableAccess(
                    domain: "finance",
                    schema: "dbo",
                    tableName: "finance_table_1",
                    justification: "{{uniqueJustification}}",
                    durationDays: 30
                ) {
                    requestId
                    status
                    message
                }
            }
            """;

        var response = await client.PostAsJsonAsync("/graphql", new { query = mutation });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("ITSM_UNAVAILABLE");

        // Verify that 0 requests exist in DB with this justification (rolled back)
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var pendingRequests = await repo.GetPendingRequestsForApproverAsync(new Sid("S-1-5-21-DATAOWNER-1"));
        pendingRequests.Any(r => r.BusinessJustification == uniqueJustification).ShouldBeFalse();
    }

    [Fact]
    public async Task Webhook_WithUnknownInstanceId_IsStrictlyRejected()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"UNKNOWN-INST-TICKET-{Guid.NewGuid():N}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-UNKNOWN-INST-USER"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-UNKNOWN-INST-USER",
            BusinessJustification = "Unknown instance test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = TenantId.LegacySingleTenant, // Even if tenant is LegacySingleTenant, unknown instance must be rejected!
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = ticketId,
            InstanceId = "malicious-unregistered-instance",
            Action = "APPROVE"
        });

        var timestamp = DateTimeOffset.UtcNow;
        var signature = ComputeSignature(payload, timestamp);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", signature);
        request.Headers.Add("X-ITSM-Timestamp", timestamp.ToString("O"));

        var response = await client.SendAsync(request);
        // Webhook handler returns false -> 401 Unauthorized
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Verify request was NOT activated
        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
    }

    [Fact]
    public async Task ServiceNowWebhook_WithNativePayload_ApprovesAndActivatesConsent()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"INC-{RandomNumberGenerator.GetInt32(100000, 999999)}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-SNOW-USER"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-SNOW-USER",
            BusinessJustification = "ServiceNow native approval test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        var client = _factory.CreateClient();
        // Native ServiceNow format: "number", "approval", "instance_name", "close_notes"
        var payload = JsonSerializer.Serialize(new
        {
            number = ticketId,
            approval = "approved",
            instance_name = "inst-tenant-a",
            close_notes = "Approved by Risk & Compliance Officer in ServiceNow",
            approved_by = "dataowner@corp.local"
        });

        var timestamp = DateTimeOffset.UtcNow;
        var signature = ComputeSignature(payload, timestamp);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/servicenow");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ServiceNow-Signature", signature);
        request.Headers.Add("X-Timestamp", timestamp.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var respJson = await response.Content.ReadFromJsonAsync<JsonElement>();
        respJson.GetProperty("status").GetString().ShouldBe("Processed");
        respJson.GetProperty("system").GetString().ShouldBe("ServiceNow");

        // Verify request is APPROVED in DB
        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("APPROVED");

        // Verify active consent exists
        var activeConsents = await repo.GetActiveConsentsForSubjectsAsync(
            [new Sid("S-1-5-21-SNOW-USER")],
            tableId,
            DateTimeOffset.UtcNow);
        activeConsents.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ServiceNowWebhook_WithStateClosedIncomplete_RejectsConsentRequest()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"CHG-{RandomNumberGenerator.GetInt32(100000, 999999)}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-SNOW-REJECT-USER"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-SNOW-REJECT-USER",
            BusinessJustification = "ServiceNow native reject test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        var client = _factory.CreateClient();
        // State 4 = Closed Incomplete (ServiceNow rejected)
        var payload = JsonSerializer.Serialize(new
        {
            number = ticketId,
            state = "4",
            instance_id = "inst-tenant-a",
            close_notes = "Security review denied the request"
        });

        var timestamp = DateTimeOffset.UtcNow;
        var signature = ComputeSignature(payload, timestamp);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/servicenow");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", signature);
        request.Headers.Add("X-ITSM-Timestamp", timestamp.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("REJECTED");
    }

    [Fact]
    public async Task JiraWebhook_WithNativePayloadAndSha256Prefix_ApprovesAndActivatesConsent()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"SEC-{RandomNumberGenerator.GetInt32(1000, 9999)}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-JIRA-USER"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-JIRA-USER",
            BusinessJustification = "Jira native approval test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        var client = _factory.CreateClient();
        // Native Jira webhook format: issue.key, issue.fields.status.name, baseUrl
        var payload = JsonSerializer.Serialize(new
        {
            webhookEvent = "jira:issue_updated",
            baseUrl = "inst-tenant-a",
            approver = "dataowner@corp.local",
            issue = new
            {
                key = ticketId,
                fields = new
                {
                    status = new { name = "Approved" },
                    resolution = new { name = "Done" }
                }
            }
        });

        var timestamp = DateTimeOffset.UtcNow;
        var rawSig = ComputeSignature(payload, timestamp);
        // Jira standard X-Hub-Signature format with sha256= prefix
        var jiraSignature = $"sha256={rawSig}";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/jira");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Hub-Signature-256", jiraSignature);
        request.Headers.Add("X-ITSM-Timestamp", timestamp.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var respJson = await response.Content.ReadFromJsonAsync<JsonElement>();
        respJson.GetProperty("status").GetString().ShouldBe("Processed");
        respJson.GetProperty("system").GetString().ShouldBe("Jira");

        // Verify request is APPROVED
        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("APPROVED");

        // Verify active consent exists
        var activeConsents = await repo.GetActiveConsentsForSubjectsAsync(
            [new Sid("S-1-5-21-JIRA-USER")],
            tableId,
            DateTimeOffset.UtcNow);
        activeConsents.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task JiraWebhook_WithStatusDeclined_RejectsConsentRequest()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"SEC-{RandomNumberGenerator.GetInt32(1000, 9999)}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-JIRA-DECLINED"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-JIRA-DECLINED",
            BusinessJustification = "Jira decline test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            webhookEvent = "jira:issue_updated",
            baseUrl = "inst-tenant-a",
            issue = new
            {
                key = ticketId,
                fields = new
                {
                    status = new { name = "Declined" },
                    resolution = new { name = "Won't Do" }
                }
            }
        });

        var timestamp = DateTimeOffset.UtcNow;
        var rawSig = ComputeSignature(payload, timestamp);
        var jiraSignature = $"sha256={rawSig}";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/jira");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Hub-Signature", jiraSignature);
        request.Headers.Add("X-Timestamp", timestamp.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("REJECTED");
    }

    [Fact]
    public async Task H06_Webhook_SignedWithLegacyGlobalSecret_IsRejected()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"GLOBAL-SECRET-{Guid.NewGuid():N}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-GLOBAL-SECRET-USER"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-GLOBAL-SECRET-USER",
            BusinessJustification = "Global secret must not be accepted",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = ticketId,
            InstanceId = "inst-tenant-a",
            Action = "APPROVE"
        });

        var timestamp = DateTimeOffset.UtcNow;
        var signature = ComputeSignature(payload, timestamp, WebhookSecret);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", signature);
        request.Headers.Add("X-ITSM-Timestamp", timestamp.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
    }
}
