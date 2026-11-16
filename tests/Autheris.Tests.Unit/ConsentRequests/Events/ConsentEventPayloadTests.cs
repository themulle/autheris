namespace Autheris.Tests.Unit.ConsentRequests.Events;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ConsentEventPayloadTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("tenant-events");

    [Fact]
    [Trait("Category", "Security")]
    public void Payload_HasNoJustificationOrApproverIdentity()
    {
        const string sensitiveJustification = "Confidential audit for CEO board review";
        const string sensitiveApproverSid = "S-1-5-21-999999-1001";
        const string sensitiveGranteeSid = "S-1-5-21-888888-2002";

        var request = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("erp", "dbo", "salaries"),
            TenantId = Tenant,
            RequesterSid = new Sid("S-1-5-21-777777-3003"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = sensitiveGranteeSid,
            BusinessJustification = sensitiveJustification,
            Status = "APPROVED",
            Version = 3,
            WorkflowMode = ConsentWorkflowMode.External,
            ExternalSystemId = Guid.NewGuid(),
            ApprovalSteps =
            [
                new ApprovalStep
                {
                    Id = Guid.NewGuid(),
                    ApproverSid = new Sid(sensitiveApproverSid),
                    Decision = "APPROVED",
                    DecidedAt = FixedNow
                }
            ]
        };

        var payload = ConsentEventPayloads.ForRequest(request, FixedNow);
        var json = ConsentEventPayloads.Serialize(payload);

        json.ShouldNotContain(sensitiveJustification, Case.Insensitive);
        json.ShouldNotContain(sensitiveApproverSid, Case.Insensitive);
        json.ShouldNotContain(sensitiveGranteeSid, Case.Insensitive);
        json.ShouldNotContain("S-1-5-21-777777-3003", Case.Insensitive);
        json.ShouldNotContain("columnRules", Case.Insensitive);
        json.ShouldNotContain("justification", Case.Insensitive);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("tenantId").GetString().ShouldBe(Tenant.Value);
        root.GetProperty("requestId").GetGuid().ShouldBe(request.Id);
        root.GetProperty("objectId").GetString().ShouldBe("erp.dbo.salaries");
        root.GetProperty("status").GetString().ShouldBe("APPROVED");
        root.GetProperty("version").GetInt32().ShouldBe(3);
        root.GetProperty("workflowMode").GetString().ShouldBe("EXTERNAL");
        root.GetProperty("links").GetProperty("self").GetString().ShouldBe($"/api/v1/consent/requests/{request.Id}");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Signature_Uses_SystemSecret_AndTimestamp()
    {
        var clock = new FakeTimeProvider(FixedNow);
        var systemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        const string protectedSecret = "enc:v1:protected-payload";
        const string plainSecret = "super-secret-hmac-key-for-system";

        var payloadJson = ConsentEventPayloads.Serialize(new ConsentThinEventPayload(
            Tenant.Value,
            Guid.NewGuid(),
            null,
            "crm.dbo.accounts",
            "PENDING_EXTERNAL_APPROVAL",
            1,
            "EXTERNAL",
            FixedNow,
            new ConsentEventLinks("/api/v1/consent/requests/1", "/api/v1/consent/events")));

        var outbox = Substitute.For<IConsentEventOutbox>();
        outbox.ClaimDeliveriesAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([
                new PendingConsentEventDelivery(
                    eventId,
                    systemId,
                    Tenant.Value,
                    ConsentEventTypes.RequestCreated,
                    eventId.ToString("D"),
                    payloadJson,
                    FixedNow,
                    Attempts: 1,
                    NextAttemptAt: FixedNow)
            ]);

        var workflowConfig = Substitute.For<IConsentWorkflowConfigRepository>();
        workflowConfig.GetExternalWorkflowSystemAsync(Tenant, systemId, Arg.Any<CancellationToken>())
            .Returns(new ExternalWorkflowSystem(
                systemId,
                Tenant.Value,
                "ServiceNow",
                "sn-client",
                "https://webhooks.example.test/consent",
                protectedSecret,
                Enabled: true,
                CreatedBy: "admin",
                CreatedAt: FixedNow,
                DisabledAt: null));

        var protector = Substitute.For<IWebhookSecretProtector>();
        protector.Unprotect(Tenant.Value, systemId.ToString("D"), protectedSecret).Returns(plainSecret);

        var handler = new CapturingHttpHandler(HttpStatusCode.Accepted);
        var gatewayOptions = new GatewayOptions();
        var retryPolicy = new WebhookRetryPolicy(gatewayOptions.Webhooks, clock, () => 1.0);
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);

        var client = new WebhookDeliveryClient(
            new HttpClient(handler),
            Options.Create(gatewayOptions),
            retryPolicy,
            NullLogger<WebhookDeliveryClient>.Instance,
            env,
            clock,
            (_, _, _) => Task.CompletedTask);

        var dispatcher = new ConsentWebhookDispatcherHostedService(
            outbox,
            workflowConfig,
            client,
            retryPolicy,
            Options.Create(new ConsentEventsOptions()),
            NullLogger<ConsentWebhookDispatcherHostedService>.Instance,
            protector,
            clock);

        var processed = await dispatcher.RunOnceAsync("worker-1", CancellationToken.None);

        processed.ShouldBe(1);
        handler.CapturedRequests.ShouldHaveSingleItem();
        var (headers, body) = handler.CapturedRequests[0];
        headers["X-Autheris-Event-Id"].ShouldBe(eventId.ToString("D"));
        headers["ce-type"].ShouldBe(ConsentEventTypes.RequestCreated);
        var timestamp = headers[WebhookSignature.TimestampHeader];
        var expectedSignature = WebhookSignature.SignHeaderValue(plainSecret, timestamp, body);
        headers[WebhookSignature.SignatureHeader].ShouldBe(expectedSignature);
        body.ShouldBe(payloadJson);

        await outbox.Received(1).MarkDeliveredAsync(
            eventId,
            systemId,
            "worker-1",
            FixedNow,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Dispatcher_DeadLetters_WhenMaxAttemptsReached()
    {
        var clock = new FakeTimeProvider(FixedNow);
        var systemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var outbox = Substitute.For<IConsentEventOutbox>();
        outbox.ClaimDeliveriesAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([
                new PendingConsentEventDelivery(
                    eventId,
                    systemId,
                    Tenant.Value,
                    ConsentEventTypes.RequestUpdated,
                    eventId.ToString("D"),
                    "{}",
                    FixedNow,
                    Attempts: 12,
                    NextAttemptAt: FixedNow)
            ]);

        var workflowConfig = Substitute.For<IConsentWorkflowConfigRepository>();
        workflowConfig.GetExternalWorkflowSystemAsync(Tenant, systemId, Arg.Any<CancellationToken>())
            .Returns(new ExternalWorkflowSystem(
                systemId, Tenant.Value, "Jira", "jira-client", "https://webhooks.example.test/consent",
                "plain-test-secret", Enabled: true, CreatedBy: "admin", CreatedAt: FixedNow, DisabledAt: null));

        var handler = new CapturingHttpHandler(HttpStatusCode.ServiceUnavailable);
        var gatewayOptions = new GatewayOptions();
        var retryPolicy = new WebhookRetryPolicy(gatewayOptions.Webhooks, clock, () => 1.0);
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);

        var client = new WebhookDeliveryClient(
            new HttpClient(handler),
            Options.Create(gatewayOptions),
            retryPolicy,
            NullLogger<WebhookDeliveryClient>.Instance,
            env,
            clock,
            (_, _, _) => Task.CompletedTask);

        var dispatcher = new ConsentWebhookDispatcherHostedService(
            outbox,
            workflowConfig,
            client,
            retryPolicy,
            Options.Create(new ConsentEventsOptions { MaxAttempts = 12 }),
            NullLogger<ConsentWebhookDispatcherHostedService>.Instance,
            secretProtector: null,
            timeProvider: clock);

        await dispatcher.RunOnceAsync("worker-1", CancellationToken.None);

        await outbox.Received(1).MarkDeadLetteredAsync(eventId, systemId, "worker-1", FixedNow, Arg.Any<CancellationToken>());
        await outbox.DidNotReceive().RescheduleDeliveryAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    private sealed class CapturingHttpHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public List<(Dictionary<string, string> Headers, string Body)> CapturedRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers)
            {
                headers[h.Key] = string.Join(",", h.Value);
            }

            var body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : string.Empty;
            CapturedRequests.Add((headers, body));
            return new HttpResponseMessage(statusCode);
        }
    }
}
