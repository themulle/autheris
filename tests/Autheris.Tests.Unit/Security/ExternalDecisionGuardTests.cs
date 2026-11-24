namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

/// <summary>Evidence validation of the external decision guard: every binding and every missing dependency fails closed.</summary>
[Trait("Category", "Security")]
public sealed class ExternalDecisionGuardTests
{
    private const string Secret = "shared-evidence-secret";
    private const string ClientId = "client-snow";
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Guid SystemId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static ExternalDecisionGuard Guard(
        bool resolver = true,
        bool replayStore = true,
        IWebhookSecretProtector? protector = null,
        bool allowPlaintext = true) =>
        new(resolver ? new FixedResolver() : null, protector, replayStore ? new MemoryReplayStore() : null, new FakeTimeProvider(Now), allowPlaintext);

    private static ExternalWorkflowSystem System(
        string tenant = "tenant-a", bool enabled = true, string? secret = Secret, string clientId = ClientId) =>
        new(SystemId, tenant, "ServiceNow", clientId, "https://snow.local/cb", secret ?? string.Empty, enabled, "admin", Now, null);

    private static ConsentRequest Request(ConsentWorkflowMode mode = ConsentWorkflowMode.External, string reference = "CHG-001", Guid? id = null) => new()
    {
        Id = id ?? Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        TenantId = TenantA,
        TableIdentifier = new TableIdentifier("sales", "dbo", "orders"),
        RequesterSid = new Sid("user:requester"),
        RequestedGranteeType = GranteeType.User,
        RequestedGranteeRef = "user:requester",
        WorkflowMode = mode,
        ExternalSystemId = SystemId,
        ExternalReference = reference,
        Status = "PENDING",
        Version = 1
    };

    private static ConsentWorkflowRouteResolution Route(Guid? systemId = null) =>
        new(ConsentWorkflowMode.External, systemId ?? SystemId, 60, 30, true, WorkflowRouteScopeKind.Tenant);

    private static ClaimsPrincipal Caller(string clientId = ClientId) =>
        new(new ClaimsIdentity([new Claim("azp", clientId)], "OAuth"));

    private static ExternalDecisionEnvelope Signed(
        ConsentRequest request,
        string secret = Secret,
        string reference = "CHG-001",
        string nonce = "nonce-1",
        int version = 1,
        string? reason = null,
        ClaimsPrincipal? caller = null,
        string subject = "alice")
    {
        var envelope = new ExternalDecisionEnvelope(
            request.Id, version, SystemId, reference, true, new AssertedApprover(subject), Now, nonce, string.Empty, reason, caller ?? Caller());
        return envelope with { Signature = ExternalDecisionGuard.ComputeSignature(secret, envelope) };
    }

    private static async Task<ConsentDecisionException> FailsAsync(
        ExternalDecisionGuard guard, ExternalDecisionEnvelope envelope, ConsentRequest request, ConsentWorkflowRouteResolution? route, ExternalWorkflowSystem? system) =>
        await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(envelope, request, route, system));

    [Fact]
    public async Task Valid_evidence_maps_the_approver_and_returns_the_evidence_reference()
    {
        var request = Request();

        var result = await Guard().ValidateAsync(Signed(request, reason: "Change approved."), request, Route(), System());

        result.Approver.UserSid.ShouldBe(new Sid("user:alice"));
        result.Evidence.ExternalSystemId.ShouldBe(SystemId);
        result.Evidence.ExternalReference.ShouldBe("CHG-001");
    }

    [Fact]
    public async Task Workflow_mode_mismatch_is_refused()
    {
        var request = Request(ConsentWorkflowMode.Internal);

        var ex = await FailsAsync(Guard(), Signed(request), request, Route(), System());

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.WorkflowModeMismatch);
        ex.StatusCode.ShouldBe(409);
    }

    [Fact]
    public async Task Disabled_missing_or_foreign_tenant_system_is_refused()
    {
        var request = Request();
        var envelope = Signed(request);

        (await FailsAsync(Guard(), envelope, request, Route(), System(enabled: false))).ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized);
        (await FailsAsync(Guard(), envelope, request, Route(), null)).ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized);
        (await FailsAsync(Guard(), envelope, request, Route(), System(tenant: "tenant-b"))).ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized);
    }

    [Fact]
    public async Task External_reference_mismatch_is_refused()
    {
        var request = Request();
        var envelope = Signed(request, reference: "CHG-999");

        var ex = await FailsAsync(Guard(), envelope, request, Route(), System());

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized);
    }

    [Fact]
    public async Task Route_system_mismatch_is_refused()
    {
        var request = Request();

        var ex = await FailsAsync(Guard(), Signed(request), request, Route(Guid.NewGuid()), System());

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized);
    }

    [Fact]
    public async Task Caller_client_id_mismatch_or_missing_is_refused()
    {
        var request = Request();

        (await FailsAsync(Guard(), Signed(request, caller: Caller("other-client")), request, Route(), System()))
            .ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized);

        var anonymous = Signed(request) with { Caller = null };
        (await FailsAsync(Guard(), anonymous, request, Route(), System()))
            .ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized);
    }

    [Fact]
    public async Task Missing_secret_protector_or_replay_store_fails_closed()
    {
        var request = Request();
        var envelope = Signed(request);

        // No stored secret at all.
        (await FailsAsync(Guard(), envelope, request, Route(), System(secret: null))).StatusCode.ShouldBe(403);

        // A protected secret without a protector cannot be opened.
        (await FailsAsync(Guard(), envelope, request, Route(), System(secret: "enc:v1:abc"))).StatusCode.ShouldBe(403);

        // A plaintext secret is only tolerated where explicitly allowed.
        (await FailsAsync(Guard(allowPlaintext: false), envelope, request, Route(), System())).StatusCode.ShouldBe(403);

        // Authentic evidence without a replay store is refused rather than accepted unchecked.
        (await FailsAsync(Guard(replayStore: false), envelope, request, Route(), System())).StatusCode.ShouldBe(403);

        // Without a realm resolver nothing can be mapped.
        (await FailsAsync(Guard(resolver: false), envelope, request, Route(), System())).ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ApproverUnresolved);
    }

    [Fact]
    public async Task Evidence_bound_to_version()
    {
        var request = Request();
        var forVersionTwo = Signed(request, version: 2);

        // The MAC of version 2 does not validate for version 1, nor for another request.
        var asVersionOne = forVersionTwo with { ExpectedVersion = 1 };
        (await FailsAsync(Guard(), asVersionOne, request, Route(), System())).ErrorCode.ShouldBe(ExternalDecisionGuard.InvalidEvidenceCode);

        var otherRequest = Request(id: Guid.NewGuid());
        var replayedOnOther = Signed(request) with { RequestId = otherRequest.Id };
        (await FailsAsync(Guard(), replayedOnOther, otherRequest, Route(), System())).ErrorCode.ShouldBe(ExternalDecisionGuard.InvalidEvidenceCode);

        // The matching version validates.
        (await Guard().ValidateAsync(Signed(request), request, Route(), System())).Approver.UserSid.ShouldBe(new Sid("user:alice"));
    }

    [Fact]
    public async Task Reason_is_part_of_the_signed_evidence()
    {
        var request = Request();
        var signed = Signed(request, reason: "Approved by CAB.");

        var tampered = signed with { Reason = "Approved by CAB. Also grant everything." };

        (await FailsAsync(Guard(), tampered, request, Route(), System())).ErrorCode.ShouldBe(ExternalDecisionGuard.InvalidEvidenceCode);
    }

    [Fact]
    public async Task Control_characters_and_oversized_fields_are_refused()
    {
        var request = Request();

        (await FailsAsync(Guard(), Signed(request, subject: "alice\nbob"), request, Route(), System())).ErrorCode.ShouldBe(ExternalDecisionGuard.InvalidEvidenceCode);
        (await FailsAsync(Guard(), Signed(request, nonce: "n\u0000x"), request, Route(), System())).ErrorCode.ShouldBe(ExternalDecisionGuard.InvalidEvidenceCode);
        (await FailsAsync(Guard(), Signed(request, subject: new string('a', 300)), request, Route(), System())).ErrorCode.ShouldBe(ExternalDecisionGuard.InvalidEvidenceCode);
        (await FailsAsync(Guard(), Signed(request, reference: "CHG\t001"), request, Route(), System())).ErrorCode.ShouldBe(ExternalDecisionGuard.InvalidEvidenceCode);
    }

    [Fact]
    public async Task Signature_is_verified_before_the_request_bindings_are_revealed()
    {
        var request = Request();
        // Wrong secret and wrong reference at once: the caller only learns that the evidence is invalid.
        var forged = Signed(request, secret: "wrong-secret", reference: "CHG-999");

        var ex = await FailsAsync(Guard(), forged, request, Route(), System());

        ex.ErrorCode.ShouldBe(ExternalDecisionGuard.InvalidEvidenceCode);
    }

    private sealed class FixedResolver : IExternalApproverResolver
    {
        public Task<SecurityPrincipalContext?> ResolveAsync(TenantId tenant, AssertedApprover asserted, CancellationToken ct = default) =>
            Task.FromResult<SecurityPrincipalContext?>(new SecurityPrincipalContext
            {
                UserSid = new Sid("user:alice"),
                TenantId = tenant,
                GroupSids = new HashSet<Sid>(),
                GroupResolutionStatus = GroupResolutionStatus.Complete,
                TenantRoles = new HashSet<string>(),
                ClusterRoles = new HashSet<string>(),
                AuthenticationScheme = "ExternalWorkflow",
                Principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.PrimarySid, "user:alice"), new Claim("tenant_id", tenant.Value)], "ExternalWorkflow"))
            });
    }

    private sealed class MemoryReplayStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, object> _items = new();
        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class =>
            Task.FromResult(_items.TryGetValue(key, out var v) ? v as T : null);
        public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class =>
            Task.FromResult(_items.TryAdd(key, value));
    }
}
