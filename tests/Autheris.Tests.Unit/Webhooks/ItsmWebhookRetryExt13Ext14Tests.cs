namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Itsm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>EXT-13: failed deliveries release the cluster dedup counters. EXT-14: APPROVED-without-activation is recoverable.</summary>
public sealed class ItsmWebhookRetryExt13Ext14Tests
{
    private sealed class NoSecrets : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) => [];
    }

    private const string Payload = "{\"ticketId\":\"CHG1\",\"action\":\"APPROVE\",\"instanceId\":\"snow\",\"eventId\":\"evt-1\"}";

    private static IOptions<GatewayOptions> BypassOptions() => Options.Create(new GatewayOptions
    {
        // The instance is bound to a tenant (an unbound instance is rejected; there is no default-tenant fallback).
        Itsm = new ItsmOptions { Enabled = true, InstanceToTenantMap = { ["snow"] = "tenant-a" } },
        Insecure = new InsecureGettingStartedOptions
        {
            danger_bypass_webhook_signature_validation = true,
            warn_ignore_webhook_timestamp_tolerance = true
        }
    });

    private static ConsentRequest Request(string status) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        ItsmTicketId = "CHG1"
    };

    [Fact]
    public async Task FailedDelivery_ReleasesClusterCounter_SoTheRetryIsProcessed()
    {
        var request = Request("PENDING_EXTERNAL_APPROVAL");
        var repo = Substitute.For<IConsentApprovalRepository>();
        repo.GetConsentRequestByTicketIdAsync("CHG1", new TenantId("tenant-a"), Arg.Any<CancellationToken>()).Returns(request);
        var approved = Request("APPROVED");
        var calls = 0;
        repo.ApproveConsentRequestStepAsync(request.Id, Arg.Any<Sid>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? Task.FromException<ConsentRequest>(new InvalidOperationException("db failover"))
                : Task.FromResult(approved));

        var handler = new ItsmWebhookHandler(
            new NoSecrets(), repo, BypassOptions(), NullLogger<ItsmWebhookHandler>.Instance,
            eventBus: null, replayCache: new ItsmWebhookReplayCache(), clusterState: new InMemoryClusterStateProvider());

        await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleStatusChangeAsync(Payload, "unused", DateTimeOffset.UtcNow));

        var retried = await handler.HandleStatusChangeAsync(Payload, "unused", DateTimeOffset.UtcNow);

        retried.ShouldBeTrue();
        calls.ShouldBe(2);
        await repo.Received(1).ActivateConsentAsync(request.Id, Arg.Any<Sid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApprovedWithoutActivation_RetryActivatesTheConsent()
    {
        var request = Request("APPROVED");
        var repo = Substitute.For<IConsentApprovalRepository>();
        repo.GetConsentRequestByTicketIdAsync("CHG1", new TenantId("tenant-a"), Arg.Any<CancellationToken>()).Returns(request);

        var handler = new ItsmWebhookHandler(
            new NoSecrets(), repo, BypassOptions(), NullLogger<ItsmWebhookHandler>.Instance,
            eventBus: null, replayCache: new ItsmWebhookReplayCache());

        var result = await handler.HandleStatusChangeAsync(Payload, "unused", DateTimeOffset.UtcNow);

        result.ShouldBeTrue();
        await repo.Received(1).ActivateConsentAsync(request.Id, Arg.Any<Sid?>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().ApproveConsentRequestStepAsync(Arg.Any<Guid>(), Arg.Any<Sid>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
