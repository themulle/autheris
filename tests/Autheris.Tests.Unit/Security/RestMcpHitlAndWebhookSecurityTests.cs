using Autheris.Domain.Security;
using Autheris.Tests.Common;
namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Security review 2026-10-02, work package D2: REST endpoints, MCP sessions, HitL and ITSM webhooks.
/// </summary>
[Trait("Category", "Security")]
public class RestMcpHitlAndWebhookSecurityTests
{
    // =========================================================================
    // C-05: HitL / Four-Eyes approval
    // =========================================================================

    private static HitLStepUpApprovalService CreateHitLService(int timeoutSeconds = 5)
    {
        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions
            {
                Enabled = true,
                ApprovalTimeoutSeconds = timeoutSeconds,
                RequireDifferentApprover = true,
                RequireTotp2Fa = false
            }
        });

        return new HitLStepUpApprovalService(options, NullLogger<HitLStepUpApprovalService>.Instance);
    }

    private static async Task<HitLApprovalTicket> WaitForPendingTicketAsync(HitLStepUpApprovalService service, string tenant)
    {
        await WaitHelper.WaitUntil(() => service.GetPendingTickets(tenant).Count > 0, TimeSpan.FromSeconds(10), "HitL ticket was not registered in time.");
        return service.GetPendingTickets(tenant)[0];
    }

    [Fact]
    public async Task C05_SelfApproval_ViaAlternativeIdentifierClaim_IsRejected()
    {
        var service = CreateHitLService();
        var table = new TableIdentifier("hr", "public", "salaries");

        // Requester identity as stored by the MCP session (GetUserSid() -> oid).
        var requestTask = service.RequestStepUpApprovalAsync("query_salaries", "tenant-a", "3f2a-oid-attacker", table);
        var ticket = await WaitForPendingTicketAsync(service, "tenant-a");

        // Same person approves via a token whose primary identifier is a different claim (PrimarySid),
        // but which also carries the oid. Before the fix only NameIdentifier was compared.
        var approver = new HitLApproverContext(
            "S-1-5-21-ATTACKER",
            ["S-1-5-21-ATTACKER", "3f2a-oid-attacker", "attacker@corp.example"],
            "tenant-a");

        var result = await service.ApproveStepUpRequestAsync(ticket.ApprovalId, approver);

        result.IsApproved.ShouldBeFalse();
        result.Message!.ShouldContain("Self-approval is strictly prohibited");
        (await service.GetTicketAsync(ticket.ApprovalId))!.Status.ShouldBe(HitLApprovalStatus.Pending);

        await service.RejectStepUpRequestAsync(ticket.ApprovalId, new HitLApproverContext("steward", ["steward"], "tenant-a"), "cleanup");
        (await requestTask).IsApproved.ShouldBeFalse();
    }

    [Fact]
    public async Task C05_CrossTenantApproval_IsTreatedAsNotFound()
    {
        var service = CreateHitLService();
        var table = new TableIdentifier("finance", "public", "wire_transfers");

        var requestTask = service.RequestStepUpApprovalAsync("transfer_funds", "tenant-a", "user-a", table);
        var ticket = await WaitForPendingTicketAsync(service, "tenant-a");

        var foreignApprover = new HitLApproverContext("steward-b", ["steward-b"], "tenant-b");
        var approveResult = await service.ApproveStepUpRequestAsync(ticket.ApprovalId, foreignApprover);
        var rejectResult = await service.RejectStepUpRequestAsync(ticket.ApprovalId, foreignApprover, "sabotage");

        approveResult.IsApproved.ShouldBeFalse();
        approveResult.Message.ShouldBe("Approval ticket not found.");
        rejectResult.Message.ShouldBe("Approval ticket not found.");
        (await service.GetTicketAsync(ticket.ApprovalId))!.Status.ShouldBe(HitLApprovalStatus.Pending);

        // Legitimate approver of the same tenant still works.
        var ok = await service.ApproveStepUpRequestAsync(ticket.ApprovalId, new HitLApproverContext("steward-a", ["steward-a"], "tenant-a"));
        ok.IsApproved.ShouldBeTrue();
        (await requestTask).IsApproved.ShouldBeTrue();
    }

    [Fact]
    public async Task C05_ClusterAdmin_MayApproveAcrossTenants()
    {
        var service = CreateHitLService();
        var requestTask = service.RequestStepUpApprovalAsync("tool", "tenant-a", "user-a", new TableIdentifier("d", "s", "t"));
        var ticket = await WaitForPendingTicketAsync(service, "tenant-a");

        var result = await service.ApproveStepUpRequestAsync(ticket.ApprovalId, new HitLApproverContext("cluster-admin", ["cluster-admin"], "tenant-x", IsCrossTenantAdmin: true));

        result.IsApproved.ShouldBeTrue();
        (await requestTask).IsApproved.ShouldBeTrue();
    }

    [Fact]
    public async Task M09_FinishedHitLTickets_ArePurgedAfterRetention()
    {
        var service = CreateHitLService();
        var requestTask = service.RequestStepUpApprovalAsync("tool", "tenant-a", "user-a", new TableIdentifier("d", "s", "t"));
        var ticket = await WaitForPendingTicketAsync(service, "tenant-a");
        (await service.ApproveStepUpRequestAsync(ticket.ApprovalId, new HitLApproverContext("steward", ["steward"], "tenant-a"))).IsApproved.ShouldBeTrue();
        await requestTask;

        service.TicketCount.ShouldBe(1);
        service.PurgeStaleTickets(DateTimeOffset.UtcNow).ShouldBe(0);

        var removed = service.PurgeStaleTickets(DateTimeOffset.UtcNow + HitLStepUpApprovalService.CompletedTicketRetention + TimeSpan.FromMinutes(1));

        removed.ShouldBe(1);
        service.TicketCount.ShouldBe(0);
    }

    [Fact]
    public void C05_PendingListing_WithoutQueryTenant_UsesOwnTenant_AndForeignTenantRequiresClusterAdmin()
    {
        var steward = CreateContext("tenant-a", ["DataSteward"], new Claim(ClaimTypes.PrimarySid, "S-1-5-21-STEWARD"));

        HitLEndpoints.ResolveListingTenant(steward, null).ShouldBe("tenant-a");
        HitLEndpoints.ResolveListingTenant(steward, "tenant-a").ShouldBe("tenant-a");
        HitLEndpoints.ResolveListingTenant(steward, "tenant-b").ShouldBeNull();

        var clusterAdmin = CreateContext("tenant-a", ["ClusterAdmin"], new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ROOT"));
        HitLEndpoints.ResolveListingTenant(clusterAdmin, "tenant-b").ShouldBe("tenant-b");
    }

    [Fact]
    public void C05_ApproverRole_IsRequired()
    {
        EndpointSecurity.IsApprover(CreateContext("tenant-a", ["Developer"]).User).ShouldBeFalse();
        EndpointSecurity.IsApprover(CreateContext("tenant-a", ["DataSteward"]).User).ShouldBeTrue();
        EndpointSecurity.IsApprover(CreateContext("tenant-a", ["DataOwner"]).User).ShouldBeTrue();
        EndpointSecurity.IsApprover(CreateContext("tenant-a", ["GovernanceAdmin"]).User).ShouldBeTrue();
    }

    [Fact]
    public void C05_ApproverContext_UsesGetUserSid_AndDetectsSelfApprovalAcrossIdentifiers()
    {
        var context = CreateContext(
            "tenant-a",
            ["DataSteward"],
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1000"),
            new Claim("oid", "0000-oid-1000"),
            new Claim(ClaimTypes.NameIdentifier, "sub-1000"),
            new Claim("upn", "user1000@corp.example"),
            new Claim("appid", "shared-spa-client"));

        var approver = HitLEndpoints.BuildApproverContext(context);

        approver.ShouldNotBeNull();
        approver.ApproverSid.ShouldBe(context.User.GetUserSid()!.Value.Value);
        approver.TenantId.ShouldBe("tenant-a");
        approver.IsCrossTenantAdmin.ShouldBeFalse();
        approver.Identifiers.ShouldContain("0000-oid-1000");
        approver.Identifiers.ShouldContain("sub-1000");
        approver.Identifiers.ShouldContain("user1000@corp.example");
        approver.Identifiers.ShouldNotContain("shared-spa-client");

        // Requester stored with any of the identifiers -> self-approval.
        HitLStepUpApprovalService.IsSameIdentity("0000-oid-1000", approver).ShouldBeTrue();
        HitLStepUpApprovalService.IsSameIdentity("sub-1000", approver).ShouldBeTrue();
        HitLStepUpApprovalService.IsSameIdentity("S-1-5-21-OTHER", approver).ShouldBeFalse();
    }

    // =========================================================================
    // H-04: CDC ingest admin substring check
    // =========================================================================

    [Fact]
    public void H04_AdminSubstringInNameOrSid_DoesNotGrantClusterAdmin()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "CORP\\badminton"),
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-READMIN"),
                new Claim(ClaimTypes.NameIdentifier, "readmin")
            ],
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role);
        var user = new ClaimsPrincipal(identity);

        StreamingCdcEndpoints.IsCdcClusterAdmin(user).ShouldBeFalse();
        StreamingCdcEndpoints.IsAuthorizedCdcIngestion(user).ShouldBeFalse();
    }

    [Fact]
    public void H04_IngestionRole_IsAuthorizedButNotClusterAdmin()
    {
        var user = CreateContext("tenant-a", ["CdcIngestionService"]).User;

        StreamingCdcEndpoints.IsAuthorizedCdcIngestion(user).ShouldBeTrue();
        StreamingCdcEndpoints.IsCdcClusterAdmin(user).ShouldBeFalse();

        StreamingCdcEndpoints.IsCdcClusterAdmin(CreateContext("tenant-a", ["ClusterAdmin"]).User).ShouldBeTrue();
    }

    // =========================================================================
    // M-09 / H-16: MCP sessions
    // =========================================================================

    [Fact]
    public void H16_RolesAreRefreshedFromCurrentPrincipal()
    {
        var store = new McpSessionStore(NullLogger<McpSessionStore>.Instance);
        var session = store.CreateSession("svc", "tenant-a", "S-1-5-21-BOB", ["GovernanceAdmin"], ["S-1-5-32-544"]);

        var refreshed = store.RefreshPrincipalContext(session.SessionId, ["Reader"], []);

        refreshed.ShouldNotBeNull();
        refreshed.Roles!.Single().ShouldBe("Reader");
        refreshed.GroupSids!.ShouldBeEmpty();
        store.GetSession(session.SessionId)!.Roles!.Single().ShouldBe("Reader");
        store.RefreshPrincipalContext("does-not-exist", ["Reader"], []).ShouldBeNull();
    }

    [Fact]
    public void M09_PerPrincipalSessionLimit_ThrowsLimitException_WithoutAffectingOtherPrincipals()
    {
        var store = new McpSessionStore(NullLogger<McpSessionStore>.Instance);
        for (var i = 0; i < McpSessionStore.MaxSessionsPerPrincipal; i++)
        {
            store.CreateSession("spa-client", "tenant-a", "S-1-5-21-FLOODER");
        }

        Should.Throw<McpSessionLimitExceededException>(() => store.CreateSession("spa-client", "tenant-a", "S-1-5-21-FLOODER"));

        // Another user of the same client is not affected.
        var other = store.CreateSession("spa-client", "tenant-a", "S-1-5-21-VICTIM");
        other.ShouldNotBeNull();

        // Closing a session frees capacity again.
        var any = store.CreateSession("spa-client-2", "tenant-a", "S-1-5-21-OTHER");
        store.RemoveSession(any.SessionId).ShouldBeTrue();
    }

    [Fact]
    public void H16_McpCaller_IsDerivedFromPrincipalAndResolvedTenant()
    {
        var context = CreateContext(
            "tenant-a",
            ["Reader"],
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-CAROL"),
            new Claim("client_id", "shared-spa"));

        var caller = McpEndpoints.ResolveCaller(context, allowOpenMcp: false);

        caller.UserSid.ShouldBe("S-1-5-21-CAROL");
        caller.TenantId.ShouldBe("tenant-a");
        caller.PrincipalId.ShouldBe("shared-spa");
        caller.Roles.ShouldContain("Reader");
    }

    [Fact]
    public void SR_M_08_McpCaller_AppOnlyToken_HasNoUserSid()
    {
        var context = CreateContext(
            "tenant-a",
            ["Reader"],
            new Claim("client_id", "shared-app"),
            new Claim("appid", "shared-app"),
            new Claim("idtyp", "app"),
            new Claim("oid", "app-oid"));

        var caller = McpEndpoints.ResolveCaller(context, allowOpenMcp: false);

        caller.UserSid.ShouldBeNull();
        caller.PrincipalId.ShouldBe("shared-app");
    }

    // =========================================================================
    // M-07: bounded body reads (chunked transfer without Content-Length)
    // =========================================================================

    [Fact]
    public async Task M07_ChunkedBodyWithoutContentLength_IsCutOffAtLimit()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[64 * 1024]);
        context.Request.ContentLength = null;

        var (body, error) = await EndpointSecurity.TryReadBodyAsync(context.Request, 16 * 1024, "too large", CancellationToken.None);

        body.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task M07_BodyWithinLimit_IsReturned()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"ok\":true}"));

        var (body, error) = await EndpointSecurity.TryReadBodyAsync(context.Request, 1024, "too large", CancellationToken.None);

        error.ShouldBeNull();
        body.ShouldBe("{\"ok\":true}");
    }

    // =========================================================================
    // M-11: global admin actions
    // =========================================================================

    [Fact]
    public void M11_GlobalAdminActions_RequireGovernanceOrClusterAdmin()
    {
        EndpointSecurity.IsGlobalGovernanceAdmin(CreateContext("tenant-a", ["DataOwner"]).User).ShouldBeFalse();
        EndpointSecurity.IsGlobalGovernanceAdmin(CreateContext("tenant-a", ["SchemaAdmin"]).User).ShouldBeFalse();
        // SEC M-11 / SEC-SIMPL-87 (OD-9): GovernanceAdmin is always tenant-bound, with or without a tenant claim
        EndpointSecurity.IsGlobalGovernanceAdmin(CreateContext("tenant-a", ["GovernanceAdmin"]).User).ShouldBeFalse();
        EndpointSecurity.IsGlobalGovernanceAdmin(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Role, "GovernanceAdmin")], "Test"))).ShouldBeFalse();
        EndpointSecurity.IsGlobalGovernanceAdmin(CreateContext("tenant-a", ["ClusterAdmin"]).User).ShouldBeTrue();
    }

    // =========================================================================
    // Helpers / fakes
    // =========================================================================

    private static DefaultHttpContext CreateContext(string tenant, string[] roles, params Claim[] extraClaims)
    {
        var claims = new List<Claim>(extraClaims);
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.Add(new Claim("tenant_id", tenant));

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role))
        };
        context.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId(tenant);
        return context;
    }

    private sealed class MapSecretProvider(Dictionary<string, string> secrets, string? fallback = null) : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef)
        {
            if (secrets.TryGetValue(secretRef, out var value))
            {
                return Encoding.UTF8.GetBytes(value);
            }

            if (fallback != null && secretRef.StartsWith("itsm:", StringComparison.OrdinalIgnoreCase))
            {
                return Encoding.UTF8.GetBytes(fallback);
            }

            throw new InvalidOperationException($"Secret '{secretRef}' not configured.");
        }
    }

    private sealed class PlaceholderSecretProvider : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) => Encoding.UTF8.GetBytes(secretRef);
    }

    private sealed class FakeConsentApprovalRepository : IConsentApprovalRepository
    {
        private readonly List<ConsentRequest> _requests = [];

        public bool KeepStatusOnActivate { get; init; }
        public bool LoseDecisionRace { get; init; }
        public List<(Guid RequestId, Sid? Actor)> Activations { get; } = [];
        public int UnscopedLookups { get; private set; }

        public ConsentRequest Add(string ticketId, string tenant)
        {
            var request = new ConsentRequest
            {
                TableId = Guid.NewGuid(),
                TableIdentifier = new TableIdentifier("d", "s", "t"),
                RequesterSid = new Sid("S-1-5-21-REQUESTER"),
                RequestedGranteeRef = "S-1-5-21-REQUESTER",
                Status = "PENDING_EXTERNAL_APPROVAL",
                ItsmTicketId = ticketId,
                TenantId = new TenantId(tenant),
                RequestedValidTo = DateTimeOffset.UtcNow.AddDays(1)
            };
            _requests.Add(request);
            return request;
        }

        public Task<ConsentRequest?> GetConsentRequestByExternalReferenceAsync(Guid externalSystemId, string externalReference, TenantId tenantId, CancellationToken ct = default)
            => Task.FromResult(_requests.FirstOrDefault(r => r.ExternalSystemId == externalSystemId && r.ExternalReference == externalReference && r.TenantId == tenantId));

        public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default)
            => ActivateConsentAsync(requestId, null, ct);

        public Task ActivateConsentAsync(Guid requestId, Sid? approvedBy, CancellationToken ct = default)
        {
            Activations.Add((requestId, approvedBy));
            if (!KeepStatusOnActivate)
            {
                _requests.First(r => r.Id == requestId).Status = "APPROVED";
            }

            return Task.CompletedTask;
        }

        public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default)
        {
            if (LoseDecisionRace)
            {
                throw new Autheris.Application.Governance.ConsentRequests.ConsentRequestStatusConflictException(requestId);
            }

            var request = _requests.First(r => r.Id == requestId);
            request.Status = "REJECTED";
            return Task.FromResult(request);
        }

        public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default)
        {
            if (LoseDecisionRace)
            {
                throw new Autheris.Application.Governance.ConsentRequests.ConsentRequestStatusConflictException(requestId);
            }

            var request = _requests.First(r => r.Id == requestId);
            var result = new ConsentRequest
            {
                Id = request.Id,
                Status = "APPROVED",
                TableId = request.TableId,
                TableIdentifier = request.TableIdentifier,
                RequesterSid = request.RequesterSid,
                TenantId = request.TenantId
            };
            if (!KeepStatusOnActivate)
            {
                request.Status = "APPROVED";
            }

            return Task.FromResult(result);
        }

        public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.FromResult(_requests.FirstOrDefault(r => r.Id == requestId));
        public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // =========================================================================
    // C11 / SEC-50-13: Config-sync webhook body SHA-256 deduplication
    // =========================================================================

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConfigSyncWebhook_replayed_body_with_new_delivery_id_is_not_retriggered()
    {
        var store = new RecordingClusterStateProvider();
        var service = CreateWebhookService(store);
        const string secret = "webhook-secret-key-for-test";
        const string payload = """{"ref":"refs/heads/main","after":"abc123"}""";
        var opts = WebhookOptions(secret);

        var firstContext = CreateWebhookHttpContext(payload, secret, "delivery-1");
        var firstResult = await VirtualFilterEndpoints.HandleConfigSyncWebhookAsync(firstContext, service, opts);

        var firstStatus = firstResult.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        firstStatus!.StatusCode.ShouldBe(StatusCodes.Status202Accepted);
        service.LastWebhookDeliveryId.ShouldBe("delivery-1");
        var firstTriggerAt = service.LastWebhookTriggerAt;
        firstTriggerAt.ShouldNotBeNull();
        store.SetKeys.Count.ShouldBe(1);
        store.SetKeys[0].Key.ShouldStartWith("vf:webhook:");
        store.SetKeys[0].Ttl.ShouldBe(TimeSpan.FromHours(24));

        var replayContext = CreateWebhookHttpContext(payload, secret, "delivery-2");
        var replayResult = await VirtualFilterEndpoints.HandleConfigSyncWebhookAsync(replayContext, service, opts);

        var replayStatus = replayResult.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        replayStatus!.StatusCode.ShouldBe(StatusCodes.Status202Accepted);
        service.LastWebhookDeliveryId.ShouldBe("delivery-1");
        service.LastWebhookTriggerAt.ShouldBe(firstTriggerAt);
        store.SetKeys.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConfigSyncWebhook_invalid_signature_does_not_store_hash()
    {
        var store = new RecordingClusterStateProvider();
        var service = CreateWebhookService(store);
        const string payload = """{"ref":"refs/heads/main","after":"abc123"}""";
        var opts = WebhookOptions("correct-secret");

        var context = CreateWebhookHttpContext(payload, "wrong-secret", "delivery-bad-sig");
        var result = await VirtualFilterEndpoints.HandleConfigSyncWebhookAsync(context, service, opts);

        var status = result.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        status!.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        store.SetKeys.ShouldBeEmpty();
        service.LastWebhookDeliveryId.ShouldBeNull();
        service.LastWebhookTriggerAt.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConfigSyncWebhook_store_failure_returns_503()
    {
        var store = new RecordingClusterStateProvider { FailOperations = true };
        var service = CreateWebhookService(store);
        const string secret = "webhook-secret-key-for-test";
        const string payload = """{"ref":"refs/heads/main","after":"abc123"}""";

        var context = CreateWebhookHttpContext(payload, secret, "delivery-outage");
        var result = await VirtualFilterEndpoints.HandleConfigSyncWebhookAsync(context, service, WebhookOptions(secret));

        var status = result.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        status!.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        service.LastWebhookDeliveryId.ShouldBeNull();
        service.LastWebhookTriggerAt.ShouldBeNull();
    }

    private static IOptions<GatewayOptions> WebhookOptions(string secret) =>
        Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { WebhookSecret = secret } });

    private static Autheris.Application.VirtualFilters.VirtualFilterAdministrationService CreateWebhookService(
        Autheris.Application.State.IDistributedClusterStateProvider clusterState) =>
        new(
            new EmptyVirtualFilterRepository(),
            new Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository(),
            Options.Create(new GatewayOptions()),
            clusterState: clusterState);

    private static DefaultHttpContext CreateWebhookHttpContext(string payload, string signingSecret, string deliveryId)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(payload);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), bodyBytes);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentLength = bodyBytes.Length;
        context.Request.Headers["X-GitHub-Delivery"] = deliveryId;
        context.Request.Headers["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexStringLower(mac);
        return context;
    }

    private sealed class EmptyVirtualFilterRepository : Autheris.Application.VirtualFilters.IVirtualFilterRepository
    {
        public Task<Autheris.Application.VirtualFilters.VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default)
            => Task.FromResult(new Autheris.Application.VirtualFilters.VirtualFilterSnapshot(1, [], []));
        public Task<long> GetGenerationAsync(CancellationToken ct = default) => Task.FromResult(1L);
        public Task ApplyAsync(Autheris.Application.VirtualFilters.VirtualFilterChangeSet changes, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingClusterStateProvider : Autheris.Application.State.IDistributedClusterStateProvider
    {
        private readonly Autheris.Application.State.InMemoryClusterStateProvider _inner = new();
        public bool FailOperations { get; init; }
        public List<(string Key, TimeSpan Ttl)> SetKeys { get; } = [];

        public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            if (FailOperations) throw new Autheris.Application.State.ClusterStateUnavailableException("Simulated store outage.");
            return _inner.GetAsync<T>(key, ct);
        }

        public ValueTask SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
        {
            if (FailOperations) throw new Autheris.Application.State.ClusterStateUnavailableException("Simulated store outage.");
            SetKeys.Add((key, ttl));
            return _inner.SetAsync(key, value, ttl, ct);
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default) => _inner.RemoveAsync(key, ct);
        public ValueTask<long?> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken ct = default) => _inner.IncrementAsync(key, delta, ttl, ct);
        public ValueTask<(Autheris.Application.State.BudgetConsumeOutcome Outcome, long ConsumedAfter)> TryConsumeBudgetAsync(string key, long cost, long limit, TimeSpan ttl, CancellationToken ct = default) => _inner.TryConsumeBudgetAsync(key, cost, limit, ttl, ct);
        public ValueTask PublishEventAsync<T>(string channel, T payload, CancellationToken ct = default) => _inner.PublishEventAsync(channel, payload, ct);
        public IAsyncDisposable SubscribeAsync<T>(string channel, Func<T, ValueTask> handler, CancellationToken ct = default) => _inner.SubscribeAsync(channel, handler, ct);
        public ValueTask<IAsyncDisposable?> TryAcquireLockAsync(string resourceKey, TimeSpan expiry, CancellationToken ct = default) => _inner.TryAcquireLockAsync(resourceKey, expiry, ct);
        public ValueTask<Autheris.Application.State.IDistributedLease?> TryAcquireLeaseAsync(string resourceKey, TimeSpan ttl, CancellationToken ct = default) => _inner.TryAcquireLeaseAsync(resourceKey, ttl, ct);
        public ValueTask<bool?> TryAcquireSlotAsync(string setKey, string member, int limit, TimeSpan ttl, CancellationToken ct = default) => _inner.TryAcquireSlotAsync(setKey, member, limit, ttl, ct);
        public ValueTask<bool> RenewSlotAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct = default) => _inner.RenewSlotAsync(setKey, member, ttl, ct);
        public ValueTask ReleaseSlotAsync(string setKey, string member, CancellationToken ct = default) => _inner.ReleaseSlotAsync(setKey, member, ct);
    }
}
