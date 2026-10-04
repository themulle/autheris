namespace Autheris.Tests.Unit;

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
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.SchemaRegistry;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Itsm;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Security review 2026-10-02, work package D2: REST endpoints, MCP sessions, HitL and ITSM webhooks.
/// </summary>
public class SecurityReview20261002EndpointTests
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
                AutoCreateItsmTicket = false
            }
        });

        return new HitLStepUpApprovalService(options, NullLogger<HitLStepUpApprovalService>.Instance);
    }

    private static async Task<HitLApprovalTicket> WaitForPendingTicketAsync(HitLStepUpApprovalService service, string tenant)
    {
        for (var i = 0; i < 100; i++)
        {
            var tickets = service.GetPendingTickets(tenant);
            if (tickets.Count > 0)
            {
                return tickets[0];
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException("HitL ticket was not registered in time.");
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

        var result = service.ApproveStepUpRequest(ticket.ApprovalId, approver);

        result.IsApproved.ShouldBeFalse();
        result.Message!.ShouldContain("Self-approval is strictly prohibited");
        service.GetTicket(ticket.ApprovalId)!.Status.ShouldBe(HitLApprovalStatus.Pending);

        service.RejectStepUpRequest(ticket.ApprovalId, new HitLApproverContext("steward", ["steward"], "tenant-a"), "cleanup");
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
        var approveResult = service.ApproveStepUpRequest(ticket.ApprovalId, foreignApprover);
        var rejectResult = service.RejectStepUpRequest(ticket.ApprovalId, foreignApprover, "sabotage");

        approveResult.IsApproved.ShouldBeFalse();
        approveResult.Message.ShouldBe("Approval ticket not found.");
        rejectResult.Message.ShouldBe("Approval ticket not found.");
        service.GetTicket(ticket.ApprovalId)!.Status.ShouldBe(HitLApprovalStatus.Pending);

        // Legitimate approver of the same tenant still works.
        var ok = service.ApproveStepUpRequest(ticket.ApprovalId, new HitLApproverContext("steward-a", ["steward-a"], "tenant-a"));
        ok.IsApproved.ShouldBeTrue();
        (await requestTask).IsApproved.ShouldBeTrue();
    }

    [Fact]
    public async Task C05_ClusterAdmin_MayApproveAcrossTenants()
    {
        var service = CreateHitLService();
        var requestTask = service.RequestStepUpApprovalAsync("tool", "tenant-a", "user-a", new TableIdentifier("d", "s", "t"));
        var ticket = await WaitForPendingTicketAsync(service, "tenant-a");

        var result = service.ApproveStepUpRequest(ticket.ApprovalId, new HitLApproverContext("cluster-admin", ["cluster-admin"], "tenant-x", IsCrossTenantAdmin: true));

        result.IsApproved.ShouldBeTrue();
        (await requestTask).IsApproved.ShouldBeTrue();
    }

    [Fact]
    public async Task M09_FinishedHitLTickets_ArePurgedAfterRetention()
    {
        var service = CreateHitLService();
        var requestTask = service.RequestStepUpApprovalAsync("tool", "tenant-a", "user-a", new TableIdentifier("d", "s", "t"));
        var ticket = await WaitForPendingTicketAsync(service, "tenant-a");
        service.ApproveStepUpRequest(ticket.ApprovalId, new HitLApproverContext("steward", ["steward"], "tenant-a")).IsApproved.ShouldBeTrue();
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
    // H-06: ITSM webhook
    // =========================================================================

    private const string SecretA = "per-instance-secret-A-7c1d";
    private const string SecretB = "per-instance-secret-B-91ef";
    private const string GlobalSecret = "legacy-global-secret-0000";

    private static IOptions<GatewayOptions> CreateItsmOptions(bool legacyGlobal = false)
        => Options.Create(new GatewayOptions
        {
            Itsm = new ItsmOptions
            {
                Enabled = true,
                LegacyGlobalWebhookSecret = legacyGlobal,
                InstanceToTenantMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["inst-a"] = "tenant-a",
                    ["inst-b"] = "tenant-b"
                }
            }
        });

    private static string Sign(string secret, DateTimeOffset ts, string payload)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"t={ts:O}.v1={payload}"))).ToLowerInvariant();

    private static string ItsmPayload(string ticketId, string? instanceId, string action = "APPROVE", string? eventId = null, string? approver = null)
        => JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["TicketId"] = ticketId,
            ["InstanceId"] = instanceId,
            ["Action"] = action,
            ["EventId"] = eventId,
            ["Approver"] = approver
        });

    private static ItsmWebhookHandler CreateHandler(
        FakeConsentApprovalRepository repo,
        IKeyVaultSecretProvider? secrets = null,
        IOptions<GatewayOptions>? options = null,
        ItsmWebhookReplayCache? cache = null)
    {
        secrets ??= new MapSecretProvider(new Dictionary<string, string>
        {
            ["itsm:webhook-secret:inst-a"] = SecretA,
            ["itsm:webhook-secret:inst-b"] = SecretB,
            ["itsm:webhook-secret"] = GlobalSecret
        });

        return new ItsmWebhookHandler(
            secrets,
            repo,
            options ?? CreateItsmOptions(),
            NullLogger<ItsmWebhookHandler>.Instance,
            eventBus: null,
            replayCache: cache ?? new ItsmWebhookReplayCache());
    }

    [Fact]
    public async Task H06_PerInstanceSecret_ValidCallback_ActivatesWithInstanceAsAuditActor()
    {
        var repo = new FakeConsentApprovalRepository();
        var request = repo.Add("SN-1", "tenant-a");
        var handler = CreateHandler(repo);
        var ts = DateTimeOffset.UtcNow;
        var payload = ItsmPayload("SN-1", "inst-a", approver: "risk.officer");

        var ok = await handler.HandleStatusChangeAsync(payload, Sign(SecretA, ts, payload), ts, null, null, CancellationToken.None);

        ok.ShouldBeTrue();
        repo.Activations.Count.ShouldBe(1);
        repo.Activations[0].RequestId.ShouldBe(request.Id);
        repo.Activations[0].Actor.HasValue.ShouldBeTrue();
        var actor = repo.Activations[0].Actor.GetValueOrDefault().Value;
        actor.ShouldContain("inst-a");
        actor.ShouldContain("risk.officer");
        actor.ShouldNotBe(request.RequesterSid.Value);
    }

    [Fact]
    public async Task H06_InstanceA_CannotApproveTicketOfTenantB()
    {
        var repo = new FakeConsentApprovalRepository();
        repo.Add("SN-B-1", "tenant-b");
        var handler = CreateHandler(repo);
        var ts = DateTimeOffset.UtcNow;
        var payload = ItsmPayload("SN-B-1", "inst-a");

        var ok = await handler.HandleStatusChangeAsync(payload, Sign(SecretA, ts, payload), ts, null, null, CancellationToken.None);

        ok.ShouldBeFalse();
        repo.Activations.ShouldBeEmpty();
        repo.UnscopedLookups.ShouldBe(0);
    }

    [Fact]
    public async Task H06_UnsignedInstanceHeader_CannotOverrideSignedPayloadInstance()
    {
        var repo = new FakeConsentApprovalRepository();
        repo.Add("SN-B-2", "tenant-b");
        var handler = CreateHandler(repo);
        var ts = DateTimeOffset.UtcNow;

        // Attack 1: payload claims inst-a (signed with A), header claims inst-b -> mismatch.
        var payload = ItsmPayload("SN-B-2", "inst-a");
        (await handler.HandleStatusChangeAsync(payload, Sign(SecretA, ts, payload), ts, "inst-b", null, CancellationToken.None)).ShouldBeFalse();

        // Attack 2: payload without instance, header selects inst-b, signature from instance A.
        var payloadWithoutInstance = ItsmPayload("SN-B-2", null);
        (await handler.HandleStatusChangeAsync(payloadWithoutInstance, Sign(SecretA, ts, payloadWithoutInstance), ts, "inst-b", null, CancellationToken.None)).ShouldBeFalse();

        repo.Activations.ShouldBeEmpty();
    }

    [Fact]
    public async Task H06_SignatureOfOtherInstance_IsRejected()
    {
        var repo = new FakeConsentApprovalRepository();
        repo.Add("SN-B-3", "tenant-b");
        var handler = CreateHandler(repo);
        var ts = DateTimeOffset.UtcNow;
        var payload = ItsmPayload("SN-B-3", "inst-b");

        (await handler.HandleStatusChangeAsync(payload, Sign(SecretA, ts, payload), ts, null, null, CancellationToken.None)).ShouldBeFalse();
        repo.Activations.ShouldBeEmpty();
    }

    [Fact]
    public async Task H06_GlobalSecret_IsOnlyAcceptedWithExplicitLegacyOption()
    {
        // Provider that only knows the global secret and returns it for every "itsm:*" alias,
        // exactly like DefaultEnvironmentSecretProvider's fallback aliases.
        var globalOnly = new MapSecretProvider(new Dictionary<string, string>(), fallback: GlobalSecret);
        var ts = DateTimeOffset.UtcNow;

        var repo = new FakeConsentApprovalRepository();
        repo.Add("SN-G-1", "tenant-a");
        var payload = ItsmPayload("SN-G-1", "inst-a");
        var signature = Sign(GlobalSecret, ts, payload);

        (await CreateHandler(repo, globalOnly).HandleStatusChangeAsync(payload, signature, ts, null, null, CancellationToken.None)).ShouldBeFalse();
        repo.Activations.ShouldBeEmpty();

        (await CreateHandler(repo, globalOnly, CreateItsmOptions(legacyGlobal: true)).HandleStatusChangeAsync(payload, signature, ts, null, null, CancellationToken.None)).ShouldBeTrue();
        repo.Activations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task H06_DevelopmentPlaceholderSecret_IsRejected()
    {
        // DefaultEnvironmentSecretProvider returns the secret reference itself as key in Development.
        var placeholder = new PlaceholderSecretProvider();
        var repo = new FakeConsentApprovalRepository();
        repo.Add("SN-P-1", "tenant-a");
        var ts = DateTimeOffset.UtcNow;
        var payload = ItsmPayload("SN-P-1", "inst-a");

        var ok = await CreateHandler(repo, placeholder).HandleStatusChangeAsync(payload, Sign("itsm:webhook-secret:inst-a", ts, payload), ts, null, null, CancellationToken.None);

        ok.ShouldBeFalse();
        repo.Activations.ShouldBeEmpty();
    }

    [Fact]
    public async Task H06_ReplayedDelivery_IsAcknowledgedButHasNoEffect()
    {
        var repo = new FakeConsentApprovalRepository { KeepStatusOnActivate = true };
        repo.Add("SN-R-1", "tenant-a");
        var handler = CreateHandler(repo);
        var ts = DateTimeOffset.UtcNow;
        var payload = ItsmPayload("SN-R-1", "inst-a", eventId: "evt-42");
        var sig = Sign(SecretA, ts, payload);

        (await handler.HandleStatusChangeAsync(payload, sig, ts, null, null, CancellationToken.None)).ShouldBeTrue();
        (await handler.HandleStatusChangeAsync(payload, sig, ts, null, null, CancellationToken.None)).ShouldBeTrue();

        // Same event id re-signed with a new timestamp is a replay as well.
        var ts2 = ts.AddSeconds(3);
        (await handler.HandleStatusChangeAsync(payload, Sign(SecretA, ts2, payload), ts2, null, null, CancellationToken.None)).ShouldBeTrue();

        repo.Activations.Count.ShouldBe(1);
    }

    [Fact]
    public void H06_ReplayCache_ExpiresEntriesAfterWindow()
    {
        var cache = new ItsmWebhookReplayCache();
        var now = DateTimeOffset.UtcNow;

        cache.TryRegister("k", now).ShouldBeTrue();
        cache.TryRegister("k", now.AddMinutes(1)).ShouldBeFalse();
        cache.TryRegister("k", now + ItsmWebhookReplayCache.Window + TimeSpan.FromSeconds(1)).ShouldBeTrue();
    }

    [Fact]
    public async Task H06_SqliteRepository_TicketLookupIsTenantBound_AndActivationOnlyFromPending()
    {
        using var repo = new SqliteGovernanceRepository(new StubEpochValidationService());
        var tableId = new TableIdentifier("sales", "crm", "h06_contacts");
        var table = new Table { Id = Guid.NewGuid(), SourceName = tableId.Domain, SchemaName = tableId.Schema, TableName = tableId.TableName, IsActive = true };
        var meta = await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableId,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "email", DataType = "VARCHAR" }]
        });

        var ticketId = $"H06-{Guid.NewGuid():N}";
        var created = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-H06"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-H06",
            BusinessJustification = "H-06 tenant binding",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(1),
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId
        });

        (await repo.GetConsentRequestByTicketIdAsync(ticketId, new TenantId("tenant-b"))).ShouldBeNull();
        (await repo.GetConsentRequestByTicketIdAsync(ticketId, new TenantId("tenant-a"))).ShouldNotBeNull();

        await repo.ActivateConsentAsync(created.Id, new Sid("ITSM_SERVICENOW:inst-a"));
        await repo.ActivateConsentAsync(created.Id, new Sid("ITSM_SERVICENOW:inst-a"));

        var active = await repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-H06")], tableId, DateTimeOffset.UtcNow);
        active.Count.ShouldBe(1);
        (await repo.GetConsentRequestAsync(created.Id))!.Status.ShouldBe("APPROVED");
    }

    // =========================================================================
    // M-09 / H-16: MCP sessions
    // =========================================================================

    [Fact]
    public void H16_SessionBinding_RejectsOtherSubjectOrTenant()
    {
        var store = new McpSessionStore(NullLogger<McpSessionStore>.Instance);
        var session = store.CreateSession("spa-client", "tenant-a", "S-1-5-21-ALICE", ["Reader"], []);

        McpSessionBinding.IsOwnedBy(session, "S-1-5-21-ALICE", "tenant-a").ShouldBeTrue();

        // Same SPA client_id, different user -> must not be able to use Alice's session.
        McpSessionBinding.IsOwnedBy(session, "S-1-5-21-MALLORY", "tenant-a").ShouldBeFalse();
        McpSessionBinding.IsOwnedBy(session, null, "tenant-a").ShouldBeFalse();
        McpSessionBinding.IsOwnedBy(session, "S-1-5-21-ALICE", "tenant-b").ShouldBeFalse();

        var anonymous = store.CreateSession("anonymous-ai-agent", "tenant-a");
        McpSessionBinding.IsOwnedBy(anonymous, "S-1-5-21-ALICE", "tenant-a").ShouldBeFalse();
        McpSessionBinding.IsOwnedBy(anonymous, null, "tenant-a").ShouldBeTrue();
    }

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
    public void H16_SessionId_IsPreferablyTakenFromHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Mcp-Session-Id"] = "from-header";
        context.Request.QueryString = new QueryString("?sessionId=from-query");

        McpEndpoints.GetSessionIdFromRequest(context.Request, preferHeader: true).ShouldBe("from-header");

        var queryOnly = new DefaultHttpContext();
        queryOnly.Request.QueryString = new QueryString("?sessionId=from-query");
        McpEndpoints.GetSessionIdFromRequest(queryOnly.Request, preferHeader: true).ShouldBe("from-query");
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
        EndpointSecurity.IsGlobalGovernanceAdmin(CreateContext("tenant-a", ["GovernanceAdmin"]).User).ShouldBeTrue();
        EndpointSecurity.IsGlobalGovernanceAdmin(CreateContext("tenant-a", ["ClusterAdmin"]).User).ShouldBeTrue();
    }

    // =========================================================================
    // M-12: schema registry ownership and RegisteredBy
    // =========================================================================

    [Fact]
    public void M12_OnlyOwnerOrFirstRegistrantMayPublish()
    {
        var history = new List<RegisteredSchema>
        {
            new() { ServiceName = "accounts", Version = "v2.0.0", Sdl = "type Query { a: Int }", RegisteredAt = DateTimeOffset.UtcNow, RegisteredBy = "S-1-5-21-OWNER" },
            new() { ServiceName = "accounts", Version = "v1.0.0", Sdl = "type Query { a: Int }", RegisteredAt = DateTimeOffset.UtcNow.AddDays(-10), RegisteredBy = "S-1-5-21-OWNER" }
        };

        SchemaRegistryEndpoints.IsServiceOwner(history, "S-1-5-21-OWNER").ShouldBeTrue();
        SchemaRegistryEndpoints.IsServiceOwner(history, "S-1-5-21-OTHER-DEV").ShouldBeFalse();
        SchemaRegistryEndpoints.IsServiceOwner(history, null).ShouldBeFalse();
        SchemaRegistryEndpoints.IsServiceOwner([], "S-1-5-21-NEW-DEV").ShouldBeTrue();
    }

    [Fact]
    public void M12_RegisteredBy_FromBody_IsReplacedByServerIdentity()
    {
        var spoofed = new SchemaRegistrationRequest
        {
            ServiceName = "accounts",
            Sdl = "type Query { a: Int }",
            RegisteredBy = "S-1-5-21-CEO",
            Version = "v9.0.0",
            DryRun = true
        };

        var serverSide = SchemaRegistryEndpoints.WithRegisteredBy(spoofed, "S-1-5-21-REAL-DEV");

        serverSide.RegisteredBy.ShouldBe("S-1-5-21-REAL-DEV");
        serverSide.ServiceName.ShouldBe("accounts");
        serverSide.Version.ShouldBe("v9.0.0");
        serverSide.DryRun.ShouldBeTrue();
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
        context.Items[TenantResolutionMiddleware.TenantIdItemKey] = new TenantId(tenant);
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

        public Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, CancellationToken ct = default)
        {
            UnscopedLookups++;
            return Task.FromResult(_requests.FirstOrDefault(r => r.ItsmTicketId == ticketId));
        }

        public Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, TenantId tenantId, CancellationToken ct = default)
            => Task.FromResult(_requests.FirstOrDefault(r => r.ItsmTicketId == ticketId && r.TenantId == tenantId));

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
            var request = _requests.First(r => r.Id == requestId);
            request.Status = "REJECTED";
            return Task.FromResult(request);
        }

        public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default)
        {
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
        public Task UpdateConsentRequestTicketIdAsync(Guid requestId, string ticketId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubEpochValidationService : IEpochValidationService
    {
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(1L);

        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(new Dictionary<TableIdentifier, long>());
    }
}
