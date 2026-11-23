namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class LegacyExternalDecisionGuardTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Guid SystemId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private static ExternalWorkflowSystem ActiveSystem(string tenant = "tenant-a", string clientId = "client-snow", bool enabled = true) =>
        new(SystemId, tenant, "ServiceNow", clientId, "https://snow.local/cb", "secret", enabled, "admin", Now, null);

    private static ConsentWorkflowRoute Route(bool requireResolvable = true, Guid? systemId = null) =>
        new(TenantA.Value, WorkflowRouteScopeKind.Tenant, TenantA.Value, ConsentWorkflowMode.External, systemId ?? SystemId, 60, 30, 72, requireResolvable, "admin", Now);

    private static ConsentRequest ExternalRequest(
        GranteeType granteeType = GranteeType.User,
        string granteeRef = "S-1-5-21-REQ",
        List<ApprovalStep>? steps = null,
        ConsentWorkflowMode mode = ConsentWorkflowMode.External) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantA,
        TableIdentifier = new TableIdentifier("sales", "dbo", "orders"),
        RequesterSid = new Sid("S-1-5-21-REQ"),
        RequesterIdentifiers = ["S-1-5-21-REQ", "req@corp.local"],
        RequestedGranteeType = granteeType,
        RequestedGranteeRef = granteeRef,
        WorkflowMode = mode,
        ExternalSystemId = SystemId,
        ExternalReference = "CHG-001",
        Status = "PENDING_EXTERNAL_APPROVAL",
        ApprovalSteps = steps ?? []
    };

    private static ConsentDecisionCommand ExternalCommand(
        Guid reqId,
        AssertedApprover? approver,
        IReadOnlyList<AssertedApprover>? additional = null,
        string externalRef = "CHG-001",
        Guid? systemId = null,
        string? callerClientId = "client-snow")
    {
        var principal = callerClientId == null
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim("azp", callerClientId)], "OAuth"));
        return new ConsentDecisionCommand(
            reqId,
            ConsentDecisionKind.Approve,
            ConsentDecisionChannel.External,
            principal,
            ExternalAttribution: new ExternalDecisionAttribution(
                systemId ?? SystemId,
                externalRef,
                "APPROVED",
                approver,
                Now,
                AdditionalApprovers: additional));
    }

    [Fact]
    public async Task ValidateAsync_InternalRequestOrMissingDisabledOrWrongClientSystem_FailsClosed()
    {
        var guard = new LegacyExternalDecisionGuard();
        var internalReq = ExternalRequest(mode: ConsentWorkflowMode.Internal);
        var modeEx = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(
            ExternalCommand(internalReq.Id, new AssertedApprover("a")), internalReq, Route(false), ActiveSystem()));
        modeEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.WorkflowModeMismatch);

        var req = ExternalRequest();

        var disabledEx = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(
            ExternalCommand(req.Id, new AssertedApprover("S-1-5-21-APP")), req, Route(false), ActiveSystem(enabled: false)));
        disabledEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized);

        var wrongRefEx = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(
            ExternalCommand(req.Id, new AssertedApprover("S-1-5-21-APP"), externalRef: "CHG-OTHER"), req, Route(false), ActiveSystem()));
        wrongRefEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized);

        var wrongClientEx = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(
            ExternalCommand(req.Id, new AssertedApprover("S-1-5-21-APP"), callerClientId: "rogue-client"), req, Route(false), ActiveSystem()));
        wrongClientEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized);
    }

    [Fact]
    public async Task ValidateAsync_RequireResolvableApprover_RefusesUnknownAndInactiveScimUsers()
    {
        var userStore = Substitute.For<IScimUserStore>();
        var guard = new LegacyExternalDecisionGuard(userStore);
        var req = ExternalRequest();

        var unknownEx = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(
            ExternalCommand(req.Id, new AssertedApprover("ghost@corp.local")), req, Route(requireResolvable: true), ActiveSystem()));
        unknownEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ApproverUnresolved);

        userStore.GetUserByUpnAsync(TenantA, "default", "inactive@corp.local", Arg.Any<CancellationToken>())
            .Returns(new ScimUser("u-off", "ext-off", "off", "Off", "inactive@corp.local", Active: false, Now, Now, Now, ObjectSid: "S-1-5-21-OFF"));

        var inactiveEx = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(
            ExternalCommand(req.Id, new AssertedApprover("off", Email: "inactive@corp.local")), req, Route(requireResolvable: true), ActiveSystem()));
        inactiveEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ApproverUnresolved);
    }

    [Fact]
    public async Task ValidateAsync_SelfApprovalOrTargetGroupMemberOrMaxDepthExceeded_IsForbidden()
    {
        var userStore = Substitute.For<IScimUserStore>();
        var groupStore = Substitute.For<IScimGroupStore>();
        var guard = new LegacyExternalDecisionGuard(userStore, groupStore);
        var req = ExternalRequest(GranteeType.Group, "S-1-5-21-TARGET-GRP");

        // Resolved SCIM user reveals requester OID even if external subject used alias
        userStore.GetUserByUpnAsync(TenantA, "default", "alias@corp.local", Arg.Any<CancellationToken>())
            .Returns(new ScimUser("u-req", null, "req", "Requester", "req@corp.local", Active: true, null, Now, Now, ObjectSid: "S-1-5-21-REQ"));

        var selfEx = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(
            ExternalCommand(req.Id, new AssertedApprover("external-alias", Email: "alias@corp.local")), req, Route(true), ActiveSystem()));
        selfEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);

        // Distinct approver who is in target group
        userStore.GetUserByObjectSidAsync(TenantA, "default", "S-1-5-21-MEM", Arg.Any<CancellationToken>())
            .Returns(new ScimUser("u-mem", null, "mem", "Member", "mem@corp.local", Active: true, null, Now, Now, ObjectSid: "S-1-5-21-MEM"));
        groupStore.GetEffectiveGroupSidsForUserAsync(TenantA, "default", "u-mem", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EffectiveScimGroupsResult(new HashSet<string>(["S-1-5-21-TARGET-GRP"]), MaxDepthExceeded: false));

        var groupEx = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(
            ExternalCommand(req.Id, new AssertedApprover("S-1-5-21-MEM")), req, Route(true), ActiveSystem()));
        groupEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);

        // Fail closed when transitive group depth exceeds limit
        groupStore.GetEffectiveGroupSidsForUserAsync(TenantA, "default", "u-mem", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EffectiveScimGroupsResult(new HashSet<string>(), MaxDepthExceeded: true));
        var depthEx = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(
            ExternalCommand(req.Id, new AssertedApprover("S-1-5-21-MEM")), req, Route(true), ActiveSystem()));
        depthEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);
    }

    [Fact]
    public async Task ValidateAsync_DuplicatePriorOrIntraPayloadApprover_RefusesFourEyes_AndAllowsDistinctApprovers()
    {
        var userStore = Substitute.For<IScimUserStore>();
        var guard = new LegacyExternalDecisionGuard(userStore);
        var req = ExternalRequest(steps: [new ApprovalStep { ApproverSid = new Sid("S-1-5-21-A1"), Decision = "APPROVED" }]);

        userStore.GetUserByUpnAsync(TenantA, "default", "a1@corp.local", Arg.Any<CancellationToken>())
            .Returns(new ScimUser("u-a1", null, "a1", "A1", "a1@corp.local", true, null, Now, Now, ObjectSid: "S-1-5-21-A1"));
        userStore.GetUserByObjectSidAsync(TenantA, "default", "S-1-5-21-A2", Arg.Any<CancellationToken>())
            .Returns(new ScimUser("u-a2", null, "a2", "A2", "a2@corp.local", true, null, Now, Now, ObjectSid: "S-1-5-21-A2"));

        var priorEx = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(
            ExternalCommand(req.Id, new AssertedApprover("a1", Email: "a1@corp.local")), req, Route(true), ActiveSystem()));
        priorEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);

        var dupPayloadEx = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(
            ExternalCommand(req.Id, new AssertedApprover("S-1-5-21-A2"), [new AssertedApprover("S-1-5-21-A2")]), req, Route(true), ActiveSystem()));
        dupPayloadEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);

        var sids = await guard.ValidateAsync(
            ExternalCommand(req.Id, new AssertedApprover("S-1-5-21-A2")), req, Route(true), ActiveSystem());
        sids.ShouldBe([new Sid("S-1-5-21-A2")]);
    }
}
