namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
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

/// <summary>L4/L6 / DEF-SC-12: the ITSM webhook has no legacy global-secret fallback and no default-tenant fallback; every rejection looks the same.</summary>
[Trait("Category", "Security")]
public sealed class ItsmWebhookNoLegacyFallbackTests
{
    private const string Payload = "{\"ticketId\":\"CHG1\",\"action\":\"APPROVE\",\"instanceId\":\"snow\",\"eventId\":\"evt-1\"}";
    private static readonly byte[] GlobalSecret = Encoding.UTF8.GetBytes("a-real-global-webhook-secret-32-bytes!");

    private sealed class GlobalSecretOnly : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) => secretRef == "itsm:webhook-secret" ? GlobalSecret : [];
    }

    private sealed class NoSecrets : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) => [];
    }

    private static IOptions<GatewayOptions> Bypass(ItsmOptions? itsm = null) => Options.Create(new GatewayOptions
    {
        Itsm = itsm ?? new ItsmOptions { Enabled = true },
        Insecure = new InsecureGettingStartedOptions
        {
            danger_bypass_webhook_signature_validation = true,
            warn_ignore_webhook_timestamp_tolerance = true
        }
    });

    private static ItsmWebhookHandler Handler(IKeyVaultSecretProvider secrets, IConsentApprovalRepository repo, IOptions<GatewayOptions> options) =>
        new(secrets, repo, options, NullLogger<ItsmWebhookHandler>.Instance, eventBus: null, replayCache: new ItsmWebhookReplayCache());

    private static Task AssertNoLookupOrMutationAsync(IConsentApprovalRepository repo, bool allowTenantScopedLookup = false)
    {
        // No mutation and no tenant-less ticket lookup: only the tenant-scoped lookup (three parameters) may happen, and only when allowed.
        foreach (var call in repo.ReceivedCalls())
        {
            var method = call.GetMethodInfo();
            (allowTenantScopedLookup && method.Name == "GetConsentRequestByTicketIdAsync" && method.GetParameters().Length == 3)
                .ShouldBeTrue($"unexpected repository call {method.Name}");
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Itsm_Webhook_Without_Instance_Secret_Is_Rejected()
    {
        // L4: a valid signature made with the global secret is not accepted; there is no global-secret fallback any more.
        var repo = Substitute.For<IConsentApprovalRepository>();
        var options = Options.Create(new GatewayOptions { Itsm = new ItsmOptions { Enabled = true } });
        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(GlobalSecret, Encoding.UTF8.GetBytes(Payload))).ToLowerInvariant();

        var accepted = await Handler(new GlobalSecretOnly(), repo, options).HandleStatusChangeAsync(Payload, signature, DateTimeOffset.UtcNow, null);

        accepted.ShouldBeFalse();
        await AssertNoLookupOrMutationAsync(repo);
    }

    [Fact]
    public async Task Itsm_Webhook_Without_Tenant_Binding_Is_Rejected()
    {
        // L6: an instance without a tenant mapping never reaches a tenant-less ticket lookup.
        var repo = Substitute.For<IConsentApprovalRepository>();
        repo.GetConsentRequestByTicketIdAsync("CHG1", Arg.Any<CancellationToken>()).Returns(new ConsentRequest { TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant, Id = Guid.NewGuid(), Status = "PENDING_EXTERNAL_APPROVAL", ItsmTicketId = "CHG1" });

        var accepted = await Handler(new NoSecrets(), repo, Bypass()).HandleStatusChangeAsync(Payload, "unused", DateTimeOffset.UtcNow, null);

        accepted.ShouldBeFalse();
        await AssertNoLookupOrMutationAsync(repo);
    }

    [Fact]
    public async Task Itsm_Webhook_Rejections_Are_Indistinguishable_From_Unknown_Instance()
    {
        // DEF-SC-12: unknown instance (no secret), unmapped tenant and a ticket of another tenant are the same observable rejection and change nothing.
        var results = new System.Collections.Generic.List<bool>();

        var repo1 = Substitute.For<IConsentApprovalRepository>();
        results.Add(await Handler(new NoSecrets(), repo1, Options.Create(new GatewayOptions { Itsm = new ItsmOptions { Enabled = true } }))
            .HandleStatusChangeAsync(Payload, "sha256=00", DateTimeOffset.UtcNow, null));
        await AssertNoLookupOrMutationAsync(repo1);

        var repo2 = Substitute.For<IConsentApprovalRepository>();
        results.Add(await Handler(new NoSecrets(), repo2, Bypass()).HandleStatusChangeAsync(Payload, "unused", DateTimeOffset.UtcNow, null));
        await AssertNoLookupOrMutationAsync(repo2);

        var repo3 = Substitute.For<IConsentApprovalRepository>();
        repo3.GetConsentRequestByTicketIdAsync("CHG1", Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns((ConsentRequest?)null);
        var mapped = new ItsmOptions { Enabled = true, InstanceToTenantMap = { ["snow"] = "tenant-a" } };
        results.Add(await Handler(new NoSecrets(), repo3, Bypass(mapped)).HandleStatusChangeAsync(Payload, "unused", DateTimeOffset.UtcNow, null));
        await AssertNoLookupOrMutationAsync(repo3, allowTenantScopedLookup: true);

        results.ShouldAllBe(r => r == false);
    }
}
