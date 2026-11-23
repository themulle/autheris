namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.ClassificationClear;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Services;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.Services;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.State;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using StackExchange.Redis;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class AdvisoryRegressionTests
{
    // GHSA-g4xx-34fc-ghq4 (details: private advisory).
    [Theory]
    [InlineData("case_01_consent_requester_and_user_sp_grantee_sod")]
    [InlineData("case_02_external_guard_user_sp_grantee_and_alias_sod")]
    [InlineData("case_03_group_grantee_and_degraded_fail_closed_consent_and_external")]
    [InlineData("case_04_access_planning_group_and_degraded_sod")]
    [InlineData("case_06_grant_policy_normalized_approver_dedup")]
    [InlineData("case_07_four_eyes_required_internal_and_external_consent_pipeline")]
    [InlineData("case_08_profile_assignment_four_eyes_and_admin_sod")]
    [InlineData("case_09_totp_replay_lockout_and_local_purge")]
    [InlineData("case_10_totp_secret_durable_redis_and_reenroll_requires_current_code")]
    public Task Ghsa_g4xx_ghq4_regression(string bullet) => bullet switch
    {
        "case_01_consent_requester_and_user_sp_grantee_sod" => AssertConsentRequesterAndGranteeSodAsync(),
        "case_02_external_guard_user_sp_grantee_and_alias_sod" => AssertExternalGuardUserSpSodAsync(),
        "case_03_group_grantee_and_degraded_fail_closed_consent_and_external" => AssertGroupAndDegradedFailClosedConsentAndExternalAsync(),
        "case_04_access_planning_group_and_degraded_sod" => AssertAccessPlanningGroupAndDegradedSodAsync(),
        "case_06_grant_policy_normalized_approver_dedup" => Task.Run(AssertGrantPolicyNormalizedApproverDedup),
        "case_07_four_eyes_required_internal_and_external_consent_pipeline" => AssertConsentPipelineFourEyesAsync(),
        "case_08_profile_assignment_four_eyes_and_admin_sod" => AssertProfileAssignmentFourEyesAndAdminSodAsync(),
        "case_09_totp_replay_lockout_and_local_purge" => AssertTotpReplayLockoutAndLocalPurgeAsync(),
        "case_10_totp_secret_durable_redis_and_reenroll_requires_current_code" => AssertTotpSecretDurabilityAndReenrollAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(bullet), bullet, null)
    };

    private static ClaimsPrincipal Wave2Principal(
        string sid,
        string? upn = null,
        string? role = null,
        string[]? groupSids = null,
        Claim[]? extraClaims = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new("tid", TenantA.Value),
            new("scp", "data.access.review")
        };
        if (upn is not null) claims.Add(new Claim(ClaimTypes.Upn, upn));
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        if (groupSids is not null)
        {
            foreach (var g in groupSids) claims.Add(IdentityClaimIssuers.CreateGroupSidClaim(g));
        }

        if (extraClaims is not null) claims.AddRange(extraClaims);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static TableMetadata Wave2PiiTable(bool highlySensitive = false)
    {
        var tableId = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            Table = new Table { Id = tableId, SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
            Columns = [new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "email", DataType = "VARCHAR" }],
            ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("email")
        };
    }

    private static ConsentRequest Wave2Request(
        string requesterSid,
        GranteeType granteeType,
        string granteeRef,
        bool external = false,
        Guid? externalSystemId = null,
        string? contextJson = null,
        string[]? requesterIdentifiers = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantA,
        TableId = Guid.NewGuid(),
        TableIdentifier = new TableIdentifier("sales", "dbo", "orders"),
        RequesterSid = new Sid(requesterSid),
        RequesterIdentifiers = requesterIdentifiers?.ToList() ?? [],
        RequestedGranteeType = granteeType,
        RequestedGranteeRef = granteeRef,
        BusinessJustification = "Need controlled access",
        RequestedValidTo = DateTimeOffset.UtcNow.AddDays(10),
        Status = external ? "PENDING_EXTERNAL_APPROVAL" : "PENDING",
        WorkflowMode = external ? ConsentWorkflowMode.External : ConsentWorkflowMode.Internal,
        ExternalSystemId = externalSystemId,
        ContextJson = contextJson
    };

    private static async Task AssertConsentRequesterAndGranteeSodAsync()
    {
        var repo = new Wave2ConsentRepo();
        var meta = Wave2PiiTable();
        repo.Tables[meta.Identifier] = meta;
        var pipeline = repo.CreatePipeline();

        var spaUserA = Wave2Principal("S-1-5-21-USER-A", upn: "alice@tenantA.local",
            extraClaims: [new Claim("appid", "portal-spa"), new Claim("azp", "portal-spa"), new Claim("client_id", "portal-spa")]);
        var extractedA = ConsentRequestService.ExtractPrincipalIdentifiers(spaUserA, "S-1-5-21-USER-A");
        extractedA.ShouldNotContain("portal-spa");
        extractedA.ShouldNotContain("alice");

        var reqSvc = new ConsentRequestService(repo.MetadataRepo, repo.ApprovalRepo);
        var reqA = Wave2Request("S-1-5-21-USER-A", GranteeType.User, "S-1-5-21-USER-A", requesterIdentifiers: extractedA.ToArray());
        repo.Requests[reqA.Id] = reqA;

        var spaUserB = Wave2Principal("S-1-5-21-USER-B", upn: "alice@tenantB.local", role: "GovernanceAdmin",
            extraClaims: [new Claim("appid", "portal-spa"), new Claim("azp", "portal-spa"), new Claim("client_id", "portal-spa")]);
        var wEx = await Should.ThrowAsync<ConsentRequestException>(() => reqSvc.WithdrawAsync(reqA.Id, spaUserB));
        wEx.Code.ShouldBe("FORBIDDEN");

        var approvedByUserB = await pipeline.DecideAsync(new ConsentDecisionCommand(reqA.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, spaUserB));
        approvedByUserB.Request.Status.ShouldBe("PENDING_SECOND_APPROVAL");

        var sameFullUpnEx = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(new ConsentDecisionCommand(
            reqA.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal,
            Wave2Principal("S-1-5-21-USER-C", upn: "ALICE@TENANTA.LOCAL", role: "GovernanceAdmin"))));
        sameFullUpnEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);

        var userTargetReq = Wave2Request("S-1-5-21-REQ-OTHER", GranteeType.User, "S-1-5-21-GRANTEE-USER");
        repo.Requests[userTargetReq.Id] = userTargetReq;
        (await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(new ConsentDecisionCommand(
            userTargetReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal,
            Wave2Principal("S-1-5-21-GRANTEE-USER", role: "GovernanceAdmin"))))).ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);

        var spTargetReq = Wave2Request("S-1-5-21-REQ-OTHER", GranteeType.ServicePrincipal, "sp-client-42");
        repo.Requests[spTargetReq.Id] = spTargetReq;
        (await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(new ConsentDecisionCommand(
            spTargetReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal,
            Wave2Principal("S-1-5-21-CLUSTER", role: "ClusterAdmin", extraClaims: [new Claim("appid", "sp-client-42")])))))
            .ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);
    }

    private static async Task AssertExternalGuardUserSpSodAsync()
    {
        var sysId = Guid.NewGuid();
        var system = new ExternalWorkflowSystem(sysId, TenantA.Value, "snow", "svc", "https://sn.local", "sec", true, "admin", DateTimeOffset.UtcNow, null);
        var route = new ConsentWorkflowRoute(TenantA.Value, WorkflowRouteScopeKind.Tenant, TenantA.Value, ConsentWorkflowMode.External, sysId, 90, 30, 72, false, "admin", DateTimeOffset.UtcNow);
        var guard = new LegacyExternalDecisionGuard();

        foreach (var (gType, gRef, subject) in new[]
        {
            (GranteeType.User, "bob@corp.local", "user:BOB@corp.local"),
            (GranteeType.ServicePrincipal, "sp-worker-9", "sp:sp-worker-9")
        })
        {
            var req = Wave2Request("S-1-5-21-REQ-1", gType, gRef, external: true, externalSystemId: sysId);
            var cmd = new ConsentDecisionCommand(
                req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.External, null,
                ExternalAttribution: new ExternalDecisionAttribution(sysId, "EXT-1", "APPROVED", new AssertedApprover(subject, ResolvedSid: subject), DateTimeOffset.UtcNow));
            var ex = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(cmd, req, route, system));
            ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);
        }
    }

    private static async Task AssertGroupAndDegradedFailClosedConsentAndExternalAsync()
    {
        const string targetGroup = "S-1-5-21-1000-2000-3000-5001";
        var sysId = Guid.NewGuid();
        var system = new ExternalWorkflowSystem(sysId, TenantA.Value, "snow", "svc", "https://sn.local", "sec", true, "admin", DateTimeOffset.UtcNow, null);
        var route = new ConsentWorkflowRoute(TenantA.Value, WorkflowRouteScopeKind.Tenant, TenantA.Value, ConsentWorkflowMode.External, sysId, 90, 30, 72, false, "admin", DateTimeOffset.UtcNow);

        EffectiveScimGroupsResult scimResult = new(new HashSet<string>(), false, false);
        var scimGroups = Wave2ScimGroupProxy((_, _) => Task.FromResult(scimResult));

        var repo = new Wave2ConsentRepo(scimGroups);
        repo.Tables[Wave2PiiTable().Identifier] = Wave2PiiTable();
        var pipeline = repo.CreatePipeline();

        var grpReq = Wave2Request("S-1-5-21-REQ-1", GranteeType.Group, targetGroup);
        repo.Requests[grpReq.Id] = grpReq;

        var directGroupClaimEx = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(new ConsentDecisionCommand(
            grpReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal,
            Wave2Principal("S-1-5-21-APP", role: "GovernanceAdmin", groupSids: [targetGroup]))));
        directGroupClaimEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);

        var degradedGroupEx = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(new ConsentDecisionCommand(
            grpReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal,
            Wave2Principal("S-1-5-21-APP", role: "GovernanceAdmin",
                extraClaims: [GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Degraded)]))));
        degradedGroupEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);

        foreach (var badResult in new[]
        {
            new EffectiveScimGroupsResult(new HashSet<string>(), MaxDepthExceeded: true, HasNonCanonicalSid: false),
            new EffectiveScimGroupsResult(new HashSet<string>(), MaxDepthExceeded: false, HasNonCanonicalSid: true),
            new EffectiveScimGroupsResult(new HashSet<string>([targetGroup]), MaxDepthExceeded: false, HasNonCanonicalSid: false)
        })
        {
            scimResult = badResult;
            var scimEx = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(new ConsentDecisionCommand(
                grpReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal,
                Wave2Principal("S-1-5-21-APP", role: "GovernanceAdmin"))));
            scimEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);

            var extReq = Wave2Request("S-1-5-21-REQ-1", GranteeType.Group, targetGroup, external: true, externalSystemId: sysId);
            var guard = new LegacyExternalDecisionGuard(groupStore: scimGroups);
            var extCmd = new ConsentDecisionCommand(
                extReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.External, null,
                ExternalAttribution: new ExternalDecisionAttribution(sysId, "EXT-2", "APPROVED", new AssertedApprover("S-1-5-21-EXT", ResolvedSid: "S-1-5-21-EXT"), DateTimeOffset.UtcNow));
            var extEx = await Should.ThrowAsync<ConsentDecisionException>(() => guard.ValidateAsync(extCmd, extReq, route, system));
            extEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);
        }

        scimResult = new EffectiveScimGroupsResult(new HashSet<string>(), false, false);
        var extReqDegraded = Wave2Request("S-1-5-21-REQ-1", GranteeType.Group, targetGroup, external: true, externalSystemId: sysId);
        var degradedCaller = Wave2Principal("svc-webhook", extraClaims: [new Claim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded)]);
        var degradedGuard = new LegacyExternalDecisionGuard(groupStore: scimGroups);
        var degradedExtEx = await Should.ThrowAsync<ConsentDecisionException>(() => degradedGuard.ValidateAsync(
            new ConsentDecisionCommand(extReqDegraded.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.External, degradedCaller,
                ExternalAttribution: new ExternalDecisionAttribution(sysId, "EXT-3", "APPROVED", new AssertedApprover("S-1-5-21-EXT", ResolvedSid: "S-1-5-21-EXT"), DateTimeOffset.UtcNow)),
            extReqDegraded, route, system));
        degradedExtEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);

        var ctxDegraded = new Autheris.Domain.Security.SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-APP"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Test"
        };
        var sodSvc = new SeparationOfDutiesService();
        (await sodSvc.CheckGroupMembershipAsync(TenantA, null, new Sid("S-1-5-21-APP"), ctxDegraded, null, targetGroup)).Allowed.ShouldBeFalse();
    }

    private static async Task AssertAccessPlanningGroupAndDegradedSodAsync()
    {
        const string targetGroup = "S-1-5-21-1000-2000-3000-5002";
        const string adminSid = "S-1-5-21-PLAN-ADMIN";
        var effectiveResult = new EffectiveScimGroupsResult(new HashSet<string>(), false, false);
        var groupStore = Wave2ScimGroupProxy((_, _) => Task.FromResult(effectiveResult));

        var cluster = new InMemoryClusterStateProvider();
        var secrets = new ClusterTotpSecretStore(cluster);
        var totp = new TotpVerificationService(cluster);
        const string seed = "JBSWY3DPEHPK3PXP";
        await secrets.SetSecretAsync(adminSid, seed);

        var metaRepo = new InMemoryTableMetadataRepository();
        var meta = Wave2PiiTable();
        meta = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "orders"),
            Table = new Table { Id = Guid.NewGuid(), SchemaName = "public", TableName = "orders", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        await metaRepo.UpsertTableMetadataAsync(meta);

        var service = new AccessPlanningService(
            new InMemoryAuditWriter(),
            tableMetadataRepository: metaRepo,
            consentRepository: new InMemoryConsentRepository(),
            totpVerificationService: totp,
            totpSecretStore: secrets,
            grantPolicy: new GrantPolicy(),
            groupStore: groupStore);

        var planReq = new AdminPlanAccessRequest(
            "sales.public.orders",
            [new PrincipalAccessGrant($"group:{targetGroup}", new Dictionary<string, string> { ["id"] = "clear" }, null, DateTimeOffset.UtcNow.AddDays(10))],
            "Team grant");

        await Should.ThrowAsync<SecurityException>(() => service.PlanAccessAsync(
            planReq, adminSid, "tenant-a", Wave2Principal(adminSid, groupSids: [targetGroup])));
        await Should.ThrowAsync<SecurityException>(() => service.PlanAccessAsync(
            planReq, adminSid, "tenant-a", Wave2Principal(adminSid, extraClaims: [new Claim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded)])));

        effectiveResult = new EffectiveScimGroupsResult(new HashSet<string>([targetGroup]), false, false);
        await Should.ThrowAsync<SecurityException>(() => service.PlanAccessAsync(planReq, adminSid, "tenant-a"));

        effectiveResult = new EffectiveScimGroupsResult(new HashSet<string>(), false, false);
        await Should.ThrowAsync<SecurityException>(() => service.PlanAccessAsync(planReq, adminSid, null));
        (await service.PlanAccessAsync(planReq, adminSid, null, Wave2Principal(adminSid))).PlanId.ShouldNotBeNullOrWhiteSpace();
        var plan = await service.PlanAccessAsync(planReq, adminSid, "tenant-a");
        var confirmed = await service.ConfirmPlanAsync(plan.PlanId, totp.GenerateTotpCode(seed), adminSid);

        effectiveResult = new EffectiveScimGroupsResult(new HashSet<string>(), false, HasNonCanonicalSid: true);
        await Should.ThrowAsync<SecurityException>(() => service.ApplyAccessAsync(
            new AdminApplyAccessRequest(plan.PlanId, confirmed.ConfirmationToken), adminSid, "tenant-a"));
    }

    private static void AssertGrantPolicyNormalizedApproverDedup()
    {
        var policy = new GrantPolicy();
        var now = DateTimeOffset.UtcNow;

        var dupApprovers = policy.Evaluate(new GrantRequest(
            GranteeType.User, "grantee-1", GrantKind.Consent, now.AddDays(10), now, GrantSensitivity.Sensitive,
            Requester: "requester@corp.com", Approvers: ["user:bob@corp.com", "BOB@CORP.COM"], Justification: "Valid"));
        dupApprovers.Allowed.ShouldBeFalse();
        dupApprovers.ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);

        var reqAliasApprover = policy.Evaluate(new GrantRequest(
            GranteeType.User, "grantee-1", GrantKind.Consent, now.AddDays(10), now, GrantSensitivity.Sensitive,
            Requester: "requester@corp.com", Approvers: ["user:requester@corp.com", "bob@corp.com"], Justification: "Valid"));
        reqAliasApprover.Allowed.ShouldBeFalse();
        reqAliasApprover.ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);

        var distinctTwo = policy.Evaluate(new GrantRequest(
            GranteeType.User, "grantee-1", GrantKind.Consent, now.AddDays(10), now, GrantSensitivity.Sensitive,
            Requester: "requester@corp.com", Approvers: ["bob@corp.com", "carol@corp.com"], Justification: "Valid"));
        distinctTwo.Allowed.ShouldBeTrue();
    }

    private static async Task AssertConsentPipelineFourEyesAsync()
    {
        var repo = new Wave2ConsentRepo();
        var meta = Wave2PiiTable(highlySensitive: false);
        repo.Tables[meta.Identifier] = meta;
        var pipeline = repo.CreatePipeline();

        IConsentApprovalRepository defaultIfaceRepo = new Autheris.Tests.Unit.ConsentRequests.InMemoryConsentStore(
            EcdsaGrantRecordSigner.CreateEphemeral("w2"), TimeProvider.System);
        await Should.ThrowAsync<NotSupportedException>(() => defaultIfaceRepo.ApproveExternalConsentRequestStepAsync(
            Guid.NewGuid(),
            new ExternalDecisionAttribution(Guid.NewGuid(), "EXT-DEF", "APPROVED", new AssertedApprover("a", ResolvedSid: "a"), DateTimeOffset.UtcNow),
            new Sid("a"),
            requireSecondApprover: true));

        var missingMetaReq = new ConsentRequest
        {
            Id = Guid.NewGuid(), TenantId = TenantA, TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("sales", "dbo", "missing_meta"),
            RequesterSid = new Sid("S-1-5-21-REQ"), RequestedGranteeType = GranteeType.User, RequestedGranteeRef = "S-1-5-21-REQ",
            BusinessJustification = "Valid", RequestedValidTo = DateTimeOffset.UtcNow.AddDays(10), Status = "PENDING"
        };
        repo.Requests[missingMetaReq.Id] = missingMetaReq;
        (await pipeline.DecideAsync(new ConsentDecisionCommand(
            missingMetaReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Wave2Principal("S-1-5-21-OWN1"))))
            .Request.Status.ShouldBe("PENDING_SECOND_APPROVAL");

        var clearCtx = RequestedClearCodec.Merge(null, [new RequestedClearItem("PII", ["email"])]);
        var internalReq = Wave2Request("S-1-5-21-REQ", GranteeType.User, "S-1-5-21-REQ", contextJson: clearCtx);
        repo.Requests[internalReq.Id] = internalReq;

        var step1 = await pipeline.DecideAsync(new ConsentDecisionCommand(
            internalReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Wave2Principal("S-1-5-21-OWN1")));
        step1.Request.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        step1.ActivatedConsent.ShouldBeNull();

        var dupEx = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(new ConsentDecisionCommand(
            internalReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Wave2Principal("S-1-5-21-OWN1"))));
        dupEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);

        var step2 = await pipeline.DecideAsync(new ConsentDecisionCommand(
            internalReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Wave2Principal("S-1-5-21-OWN2")));
        step2.Request.Status.ShouldBe("APPROVED");
        step2.ActivatedConsent.ShouldNotBeNull();

        var extReq = Wave2Request("S-1-5-21-REQ", GranteeType.User, "S-1-5-21-REQ", external: true, externalSystemId: repo.ExternalSystemId);
        repo.Requests[extReq.Id] = extReq;
        var extStep1 = await pipeline.DecideAsync(new ConsentDecisionCommand(
            extReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.External, null,
            ExternalAttribution: new ExternalDecisionAttribution(repo.ExternalSystemId, "EXT-1", "APPROVED", new AssertedApprover("S-1-5-21-OWN1", ResolvedSid: "S-1-5-21-OWN1"), DateTimeOffset.UtcNow)));
        extStep1.Request.Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
        extStep1.ActivatedConsent.ShouldBeNull();

        var extStep2 = await pipeline.DecideAsync(new ConsentDecisionCommand(
            extReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.External, null,
            ExternalAttribution: new ExternalDecisionAttribution(repo.ExternalSystemId, "EXT-2", "APPROVED", new AssertedApprover("S-1-5-21-OWN2", ResolvedSid: "S-1-5-21-OWN2"), DateTimeOffset.UtcNow)));
        extStep2.Request.Status.ShouldBe("APPROVED");
        extStep2.ActivatedConsent.ShouldNotBeNull();
    }

    private static async Task AssertProfileAssignmentFourEyesAndAdminSodAsync()
    {
        var h = new Wave2ProfileHarness();
        var reqInternal = h.SeedPendingRequest("S-1-5-21-REQ", GranteeType.User, "S-1-5-21-GRANTEE", MaskingPolicyMode.Default, external: false);

        var adminSelfEx = await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.DecideAsync(
            new ConsentDecisionCommand(reqInternal.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal,
                Wave2Principal("S-1-5-21-GRANTEE", role: "GovernanceAdmin")),
            reqInternal, new Sid("S-1-5-21-GRANTEE"), []));
        adminSelfEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);

        var step1 = await h.Service.DecideAsync(
            new ConsentDecisionCommand(reqInternal.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Wave2Principal("S-1-5-21-OWN1")),
            reqInternal, new Sid("S-1-5-21-OWN1"), []);
        step1.Request.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        h.Consents.ShouldBeEmpty();

        var updatedReq = h.Requests.Single(r => r.Id == reqInternal.Id);
        var sameEx = await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.DecideAsync(
            new ConsentDecisionCommand(reqInternal.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Wave2Principal("S-1-5-21-OWN1")),
            updatedReq, new Sid("S-1-5-21-OWN1"), []));
        sameEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);

        var step2 = await h.Service.DecideAsync(
            new ConsentDecisionCommand(reqInternal.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Wave2Principal("S-1-5-21-OWN2")),
            updatedReq, new Sid("S-1-5-21-OWN2"), []);
        step2.Request.Status.ShouldBe("APPROVED");
        h.Consents.Count.ShouldBe(1);

        var reqExt = h.SeedPendingRequest("S-1-5-21-REQ", GranteeType.User, "S-1-5-21-GRANTEE2", MaskingPolicyMode.Default, external: true);
        var extStep1 = await h.Service.DecideAsync(
            new ConsentDecisionCommand(reqExt.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.External, null,
                ExternalAttribution: new ExternalDecisionAttribution(Guid.NewGuid(), "EXT-P1", "APPROVED", new AssertedApprover("S-1-5-21-EXT1", ResolvedSid: "S-1-5-21-EXT1"), DateTimeOffset.UtcNow)),
            reqExt, new Sid("S-1-5-21-EXT1"), [new Sid("S-1-5-21-EXT1")]);
        extStep1.Request.Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
        h.Consents.Count.ShouldBe(1);

        var updatedExt = h.Requests.Single(r => r.Id == reqExt.Id);
        var extStep2 = await h.Service.DecideAsync(
            new ConsentDecisionCommand(reqExt.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.External, null,
                ExternalAttribution: new ExternalDecisionAttribution(Guid.NewGuid(), "EXT-P2", "APPROVED", new AssertedApprover("S-1-5-21-EXT2", ResolvedSid: "S-1-5-21-EXT2"), DateTimeOffset.UtcNow)),
            updatedExt, new Sid("S-1-5-21-EXT2"), [new Sid("S-1-5-21-EXT2")]);
        extStep2.Request.Status.ShouldBe("APPROVED");
        h.Consents.Count.ShouldBe(2);

        var reqExtDual = h.SeedPendingRequest("S-1-5-21-REQ", GranteeType.User, "S-1-5-21-GRANTEE3", MaskingPolicyMode.Default, external: true);
        var dualResult = await h.Service.DecideAsync(
            new ConsentDecisionCommand(reqExtDual.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.External, null,
                ExternalAttribution: new ExternalDecisionAttribution(Guid.NewGuid(), "EXT-DUAL", "APPROVED",
                    new AssertedApprover("S-1-5-21-EXT1", ResolvedSid: "S-1-5-21-EXT1"), DateTimeOffset.UtcNow,
                    AdditionalApprovers: [new AssertedApprover("S-1-5-21-EXT2", ResolvedSid: "S-1-5-21-EXT2")])),
            reqExtDual, new Sid("S-1-5-21-EXT2"), [new Sid("S-1-5-21-EXT1"), new Sid("S-1-5-21-EXT2")]);
        dualResult.Request.Status.ShouldBe("APPROVED");
        h.Requests.Single(r => r.Id == reqExtDual.Id).ApprovalSteps.Select(s => s.ApproverSid.Value)
            .ShouldBe(["S-1-5-21-EXT1", "S-1-5-21-EXT2"]);

        var reqMulti = h.SeedPendingRequest("S-1-5-21-REQ", GranteeType.User, "S-1-5-21-GRANTEE4", MaskingPolicyMode.Default, external: false, multiItem: true);
        await h.Service.DecideAsync(
            new ConsentDecisionCommand(reqMulti.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Wave2Principal("S-1-5-21-OWN1")),
            reqMulti, new Sid("S-1-5-21-OWN1"), []);
        var reqMultiAfter1 = h.Requests.Single(r => r.Id == reqMulti.Id);
        reqMultiAfter1.Status.ShouldBe("PENDING");

        var afterReject2 = await h.Service.DecideAsync(
            new ConsentDecisionCommand(reqMulti.Id, ConsentDecisionKind.Reject, ConsentDecisionChannel.Internal, Wave2Principal("S-1-5-21-PUBOWN"), Reason: "Deny public table"),
            reqMultiAfter1, new Sid("S-1-5-21-PUBOWN"), []);
        afterReject2.Request.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        h.Consents.Count.ShouldBe(3);

        var reqMultiAfter2 = h.Requests.Single(r => r.Id == reqMulti.Id);
        var multiFinal = await h.Service.DecideAsync(
            new ConsentDecisionCommand(reqMulti.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Wave2Principal("S-1-5-21-OWN3")),
            reqMultiAfter2, new Sid("S-1-5-21-OWN3"), []);
        multiFinal.Request.Status.ShouldBe("APPROVED");
        h.Consents.Count.ShouldBe(4);
    }

    private static async Task AssertTotpReplayLockoutAndLocalPurgeAsync()
    {
        var svc = new TotpVerificationService(options: new TotpOptions { MaxFailedAttempts = 3, LockoutMinutes = 5 });
        const string secret = "JBSWY3DPEHPK3PXP";
        var codeNow = svc.GenerateTotpCode(secret, DateTimeOffset.UtcNow);

        (await svc.VerifyAndConsumeTotpAsync("S-1-5-21-U1", secret, codeNow)).ShouldBeTrue();
        for (int i = 0; i < 3; i++)
        {
            (await svc.VerifyAndConsumeTotpAsync("S-1-5-21-U1", secret, codeNow)).ShouldBeFalse();
        }

        var nextStepCode = svc.GenerateTotpCode(secret, DateTimeOffset.UtcNow.AddSeconds(30));
        (await svc.VerifyAndConsumeTotpAsync("S-1-5-21-U1", secret, nextStepCode)).ShouldBeFalse();

        var field = typeof(TotpVerificationService).GetField("_localFailures", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var map = (IDictionary)field.GetValue(svc)!;
        var tupleType = typeof(TotpVerificationService).GetField("_localFailures", BindingFlags.Instance | BindingFlags.NonPublic)!
            .FieldType.GenericTypeArguments[1];
        map["totp:fail:stale-user"] = Activator.CreateInstance(tupleType, 4, DateTimeOffset.UtcNow.AddSeconds(-5))!;

        var freshCode = svc.GenerateTotpCode(secret, DateTimeOffset.UtcNow);
        (await svc.VerifyAndConsumeTotpAsync("S-1-5-21-FRESH", secret, freshCode)).ShouldBeTrue();
        map.Contains("totp:fail:stale-user").ShouldBeFalse();
    }

    private static async Task AssertTotpSecretDurabilityAndReenrollAsync()
    {
        var stringSetCalls = new List<(string Method, object? Expiry)>();
        var dbProxy = Wave2Proxy<IDatabase>.Create((m, args) =>
        {
            if (m.Name == "StringSetAsync")
            {
                stringSetCalls.Add((m.Name, args is { Length: >= 3 } ? args[2] : null));
                return Task.FromResult(true);
            }

            return m.ReturnType == typeof(Task<bool>) ? Task.FromResult(true) : null;
        });
        var muxProxy = Wave2Proxy<IConnectionMultiplexer>.Create((m, _) =>
            m.Name == "GetDatabase" ? dbProxy : null);

        var redis = new RedisClusterStateProvider(muxProxy, Options.Create(new GatewayOptions()), NullLogger<RedisClusterStateProvider>.Instance);
        await redis.SetAsync("totp:secret:u1", "SEED", TimeSpan.MaxValue);
        await redis.SetAsync("totp:secret:u2", "SEED", TimeSpan.FromMinutes(10));

        stringSetCalls.Count.ShouldBe(2);
        (stringSetCalls[0].Expiry is Expiration exp0 && exp0.Equals(default(Expiration))).ShouldBeTrue();
        (stringSetCalls[1].Expiry is TimeSpan ts1 && ts1 == TimeSpan.FromMinutes(10)
            || (stringSetCalls[1].Expiry is Expiration exp1 && !exp1.Equals(default(Expiration)))).ShouldBeTrue();

        TimeSpan? recordedTtl = null;
        var stateSpy = Wave2Proxy<IDistributedClusterStateProvider>.Create((m, args) =>
        {
            if (m.Name == "SetAsync")
            {
                recordedTtl = (TimeSpan)args![2]!;
                return ValueTask.CompletedTask;
            }

            return null;
        });
        var secretStore = new ClusterTotpSecretStore(stateSpy);
        await secretStore.SetSecretAsync("S-1-5-21-U2", "JBSWY3DPEHPK3PXP");
        recordedTtl.ShouldBe(TimeSpan.MaxValue);

        var realStore = new ClusterTotpSecretStore();
        var totp = new TotpVerificationService();
        var enroll = new TotpEnrollmentService(totp, realStore);
        var b1 = (await enroll.BeginEnrollmentAsync("S-1-5-21-U3", "u3@corp.local", "Autheris"))!;
        (await enroll.CompleteEnrollmentAsync("S-1-5-21-U3", totp.GenerateTotpCode(b1.SecretBase32), null)).ShouldBe(TotpEnrollmentOutcome.Completed);

        var b2 = (await enroll.BeginEnrollmentAsync("S-1-5-21-U3", "u3@corp.local", "Autheris"))!;
        (await enroll.CompleteEnrollmentAsync("S-1-5-21-U3", totp.GenerateTotpCode(b2.SecretBase32), null))
            .ShouldBe(TotpEnrollmentOutcome.CurrentCodeRequired);

        await realStore.RemoveSecretAsync("S-1-5-21-U3");
        (await enroll.CompleteEnrollmentAsync(
            "S-1-5-21-U3",
            totp.GenerateTotpCode(b2.SecretBase32, DateTimeOffset.UtcNow.AddSeconds(30)),
            null)).ShouldBe(TotpEnrollmentOutcome.Completed);
    }

    private static IScimGroupStore Wave2ScimGroupProxy(
        Func<string, CancellationToken, Task<EffectiveScimGroupsResult>> onEffective) =>
        Wave2Proxy<IScimGroupStore>.Create((m, args) => m.Name switch
        {
            "GetEffectiveGroupSidsForUserAsync" => onEffective((string)args![2]!, (CancellationToken)args[4]!),
            "GetGroupMembershipsForUserAsync" => Task.FromResult<IReadOnlyList<string>>([]),
            _ => throw new NotSupportedException(m.Name)
        });

#pragma warning disable CA1852 // DispatchProxy requires an unsealed proxy type
    private class Wave2Proxy<T> : DispatchProxy where T : class
#pragma warning restore CA1852
    {
        private Func<MethodInfo, object?[]?, object?> _handler = null!;
        public static T Create(Func<MethodInfo, object?[]?, object?> handler)
        {
            var proxy = Create<T, Wave2Proxy<T>>();
            ((Wave2Proxy<T>)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _handler(targetMethod!, args);
    }

    private sealed class Wave2ConsentRepo
    {
        public Dictionary<Guid, ConsentRequest> Requests { get; } = new();
        public Dictionary<TableIdentifier, TableMetadata> Tables { get; } = new();
        public Guid ExternalSystemId { get; } = Guid.NewGuid();

        public IConsentApprovalRepository ApprovalRepo { get; }
        public ITableMetadataRepository MetadataRepo { get; }

        public ConsentDecisionPipeline CreatePipeline()
        {
            return new ConsentDecisionPipeline(
                ApprovalRepo, MetadataRepo, _owners, new ConsentDecisionGuard(new IdentitySubjectResolver()),
                new GrantPolicy(), GrantValidityPolicy.CreateDefault(),
                new LegacyExternalDecisionGuard(groupStore: _scimGroups),
                workflowConfigRepository: _workflow, groupStore: _scimGroups,
                classificationRepository: new Autheris.Tests.Unit.Governance.Classification.TestInMemoryClassificationStore(TenantA.Value));
        }

        private readonly IDataOwnershipRepository _owners;
        private readonly IConsentWorkflowConfigRepository _workflow;
        private readonly IScimGroupStore? _scimGroups;

        public Wave2ConsentRepo(IScimGroupStore? scimGroups = null)
        {
            _scimGroups = scimGroups;
            ApprovalRepo = Wave2Proxy<IConsentApprovalRepository>.Create((m, args) => m.Name switch
            {
                "GetConsentRequestAsync" => Task.FromResult(Requests.TryGetValue((Guid)args![0]!, out var r) ? r : null),
                "ApproveConsentRequestStepAsync" => ApproveStepAsync((Guid)args![0]!, (Sid)args[1]!, args.Length >= 4 && (bool)args[2]!, isExternal: false),
                "ApproveExternalConsentRequestStepAsync" => ApproveStepAsync((Guid)args![0]!, (Sid)args[2]!, args.Length >= 5 && (bool)args[3]!, isExternal: true),
                "ExecuteAtomicApprovalWithConsentAsync" => CompleteAsync((Guid)args![0]!, (ApprovalStep)args[1]!, (Consent)args[2]!),
                "ExecuteExternalAtomicApprovalWithConsentAsync" => CompleteAsync((Guid)args![0]!, (ApprovalStep)args[2]!, (Consent)args[3]!),
                _ => throw new NotSupportedException(m.Name)
            });
            MetadataRepo = Wave2Proxy<ITableMetadataRepository>.Create((m, args) => m.Name == "GetTableMetadataAsync"
                ? Task.FromResult(Tables.TryGetValue((TableIdentifier)args![0]!, out var tm) ? tm : null)
                : throw new NotSupportedException(m.Name));
            _owners = Wave2Proxy<IDataOwnershipRepository>.Create((m, _) => m.Name == "IsAuthorizedApproverForTableAsync"
                ? Task.FromResult(true) : Task.FromResult<IReadOnlyList<DataOwner>>([]));
            _workflow = Wave2Proxy<IConsentWorkflowConfigRepository>.Create((m, args) => m.Name switch
            {
                "ResolveWorkflowRouteAsync" => Task.FromResult<ConsentWorkflowRoute?>(new ConsentWorkflowRoute(TenantA.Value, WorkflowRouteScopeKind.Tenant, TenantA.Value, ConsentWorkflowMode.External, ExternalSystemId, 90, 30, 72, false, "admin", DateTimeOffset.UtcNow)),
                "GetExternalWorkflowSystemAsync" => Task.FromResult<ExternalWorkflowSystem?>(new ExternalWorkflowSystem(ExternalSystemId, TenantA.Value, "snow", "svc", "https://sn.local", "sec", true, "admin", DateTimeOffset.UtcNow, null)),
                _ => throw new NotSupportedException(m.Name)
            });
        }

        private Task<ConsentRequest> ApproveStepAsync(Guid id, Sid approver, bool requireSecond, bool isExternal)
        {
            var r = Requests[id];
            r.ApprovalSteps.Add(new ApprovalStep { Id = Guid.NewGuid(), ConsentRequestId = id, StepNumber = r.ApprovalSteps.Count + 1, ApproverSid = approver, Decision = "APPROVED" });
            r.Status = isExternal ? "PENDING_EXTERNAL_APPROVAL" : (requireSecond ? "PENDING_SECOND_APPROVAL" : "APPROVED");
            return Task.FromResult(r);
        }

        private Task<Consent> CompleteAsync(Guid id, ApprovalStep step, Consent c)
        {
            Requests[id].ApprovalSteps.Add(step);
            Requests[id].Status = "APPROVED";
            return Task.FromResult(c);
        }
    }

    private sealed class Wave2ProfileHarness
    {
        private readonly List<ProfileAssignmentItemRow> _items = [];
        public List<ConsentRequest> Requests { get; } = [];
        public List<Consent> Consents { get; } = [];
        public ProfileAssignmentRequestService Service { get; }

        public Wave2ProfileHarness()
        {
            var table = Wave2PiiTable(highlySensitive: false);
            var pubTable = new TableMetadata
            {
                Identifier = new TableIdentifier("sales", "dbo", "orders_pub"),
                Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "orders_pub", IsActive = true },
                Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "INT" }]
            };
            var profiles = Wave2Proxy<IAccessProfileRepository>.Create((_, _) => throw new NotSupportedException());
            var meta = new InMemoryTableMetadataRepository().Add(table).Add(pubTable);
            var approvals = Wave2Proxy<IConsentApprovalRepository>.Create((_, _) => throw new NotSupportedException());
            var owners = new InMemoryDataOwnershipRepository()
                .AssignOwner(table.Identifier, new DataOwner { Id = Guid.NewGuid(), AdSid = new Sid("S-1-5-21-OWN1"), DisplayName = "O1", Email = "o1@x", IsActive = true })
                .AssignOwner(table.Identifier, new DataOwner { Id = Guid.NewGuid(), AdSid = new Sid("S-1-5-21-OWN2"), DisplayName = "O2", Email = "o2@x", IsActive = true })
                .AssignOwner(table.Identifier, new DataOwner { Id = Guid.NewGuid(), AdSid = new Sid("S-1-5-21-OWN3"), DisplayName = "O3", Email = "o3@x", IsActive = true })
                .AssignOwner(pubTable.Identifier, new DataOwner { Id = Guid.NewGuid(), AdSid = new Sid("S-1-5-21-PUBOWN"), DisplayName = "PO", Email = "po@x", IsActive = true });
            var tx = new Wave2DbTx();
            var store = Wave2Proxy<IProfileAssignmentStore>.Create((m, args) => m.Name switch
            {
                "BeginTransactionAsync" => Task.FromResult<IProfileAssignmentTransaction>(tx),
                "GetAssignmentItemsAsync" => Task.FromResult<IReadOnlyList<ProfileAssignmentItemRow>>(_items.Where(i => i.RequestId == (Guid)args![1]!).ToList()),
                "DecideAssignmentItemsAsync" => DecideItemsAsync((Guid)args![1]!, (IReadOnlyCollection<string>)args[2]!, (string)args[3]!, (string)args[4]!, (DateTimeOffset)args[5]!, (bool)args[6]!),
                "ApplyRequestStepAndStatusInTransactionAsync" => ApplyStepAsync((ConsentRequest)args![1]!, (string?)args[2], (Sid)args[3]!, (string)args[4]!),
                "InvalidateTablesAfterCommitAsync" => Task.CompletedTask,
                _ => throw new NotSupportedException(m.Name)
            });
            var materializer = Wave2Proxy<IProfileGrantMaterializer>.Create((m, args) =>
            {
                if (m.Name != "MaterializeApprovedAsync") throw new NotSupportedException(m.Name);
                var c = new Consent { Id = Guid.NewGuid(), TenantId = TenantA, TableIdentifier = table.Identifier, ValidTo = DateTimeOffset.UtcNow.AddDays(10) };
                Consents.Add(c);
                return Task.FromResult<IReadOnlyList<Consent>>([c]);
            });
            Service = new ProfileAssignmentRequestService(
                profiles, meta, approvals, owners, store, materializer, new GrantPolicy(), GrantValidityPolicy.CreateDefault());
        }

        public ConsentRequest SeedPendingRequest(string requester, GranteeType gType, string gRef, MaskingPolicyMode mode, bool external, bool multiItem = false)
        {
            var req = Wave2Request(requester, gType, gRef, external: external);
            var snapItems = multiItem
                ? new[] { new ProfileAssignmentSnapshotItem(0, "sales.dbo.orders", false, null, []), new ProfileAssignmentSnapshotItem(1, "sales.dbo.orders_pub", false, null, []) }
                : [new ProfileAssignmentSnapshotItem(0, "sales.dbo.orders", false, null, [])];
            var snap = new ProfileAssignmentRequestContext(Guid.NewGuid(), "ProfilePii", mode, [], snapItems);
            req = new ConsentRequest
            {
                Id = req.Id, TenantId = TenantA, TableId = req.TableId, TableIdentifier = req.TableIdentifier,
                RequesterSid = new Sid(requester), RequestedGranteeType = gType, RequestedGranteeRef = gRef,
                BusinessJustification = "Valid justification", RequestedValidTo = DateTimeOffset.UtcNow.AddDays(10),
                Status = req.Status, WorkflowMode = req.WorkflowMode, RequestKind = ConsentRequestKind.ProfileAssignment,
                ContextJson = System.Text.Json.JsonSerializer.Serialize(snap)
            };
            Requests.Add(req);
            _items.Add(new ProfileAssignmentItemRow(TenantA.Value, req.Id, 0, "sales.dbo.orders", "S-1-5-21-OWN1", "PENDING"));
            if (multiItem) _items.Add(new ProfileAssignmentItemRow(TenantA.Value, req.Id, 1, "sales.dbo.orders_pub", "S-1-5-21-PUBOWN", "PENDING"));
            return req;
        }

        private Task<int> DecideItemsAsync(Guid id, IReadOnlyCollection<string> owners, string decision, string by, DateTimeOffset at, bool decideAll)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].RequestId == id && _items[i].OwnerDecision == "PENDING" && (decideAll || owners.Contains(_items[i].OwnerSid, StringComparer.OrdinalIgnoreCase)))
                    _items[i] = _items[i] with { OwnerDecision = decision, DecidedBy = by, DecidedAt = at };
            }

            return Task.FromResult(1);
        }

        private Task ApplyStepAsync(ConsentRequest req, string? newStatus, Sid approver, string decision)
        {
            var idx = Requests.FindIndex(r => r.Id == req.Id);
            var existing = Requests[idx];
            var steps = existing.ApprovalSteps.Concat([new ApprovalStep { Id = Guid.NewGuid(), ConsentRequestId = req.Id, StepNumber = existing.ApprovalSteps.Count + 1, ApproverSid = approver, Decision = decision }]).ToList();
            Requests[idx] = new ConsentRequest
            {
                Id = existing.Id, TenantId = existing.TenantId, TableId = existing.TableId, TableIdentifier = existing.TableIdentifier,
                RequesterSid = existing.RequesterSid, RequestedGranteeType = existing.RequestedGranteeType, RequestedGranteeRef = existing.RequestedGranteeRef,
                BusinessJustification = existing.BusinessJustification, RequestedValidTo = existing.RequestedValidTo,
                Status = string.IsNullOrWhiteSpace(newStatus) ? existing.Status : newStatus,
                WorkflowMode = existing.WorkflowMode, RequestKind = existing.RequestKind, ContextJson = existing.ContextJson, ApprovalSteps = steps
            };
            return Task.CompletedTask;
        }

        private sealed class Wave2DbTx : DbTransaction, IProfileAssignmentTransaction
        {
            public DbTransaction Transaction => this;
            public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
            protected override DbConnection? DbConnection => null;
            public override void Commit() { }
            public override void Rollback() { }
        }
    }
}
