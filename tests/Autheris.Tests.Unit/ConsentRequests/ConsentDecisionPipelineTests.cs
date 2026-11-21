namespace Autheris.Tests.Unit.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ConsentDecisionPipelineTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid RequesterSid = new("S-1-5-21-REQ");
    private static readonly Sid Approver1Sid = new("S-1-5-21-APP1");
    private static readonly Sid Approver2Sid = new("S-1-5-21-APP2");
    private static readonly Guid ExternalSystemId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal Principal(Sid sid, string tenant = "tenant-a", params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid.Value),
            new("tenant_id", tenant)
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", ClaimTypes.Name, ClaimTypes.Role));
    }

    private static TableMetadata TableMeta(bool fourEyes = false, string sensitivity = "NORMAL")
    {
        var tid = new TableIdentifier("sales", "dbo", "orders");
        var id = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = tid,
            Table = new Table
            {
                Id = id,
                SourceName = tid.Domain,
                SchemaName = tid.Schema,
                TableName = tid.TableName,
                Sensitivity = sensitivity,
                RequiresFourEyes = fourEyes
            },
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), TableId = id, ColumnName = "id", DataType = "INT" },
                new TableColumn { Id = Guid.NewGuid(), TableId = id, ColumnName = "amount", DataType = "DECIMAL" }
            ]
        };
    }

    private static ConsentRequest Request(
        ConsentWorkflowMode mode = ConsentWorkflowMode.Internal,
        string status = "PENDING",
        int version = 1,
        string tenant = "tenant-a",
        GranteeType granteeType = GranteeType.User,
        string? granteeRef = null,
        List<ApprovalStep>? steps = null,
        Sid? requester = null,
        TableIdentifier? table = null) => new()
    {
        Id = Guid.NewGuid(),
        TableId = Guid.NewGuid(),
        TableIdentifier = table ?? new TableIdentifier("sales", "dbo", "orders"),
        RequesterSid = requester ?? RequesterSid,
        RequestedGranteeType = granteeType,
        RequestedGranteeRef = granteeRef ?? (requester ?? RequesterSid).Value,
        BusinessJustification = "Pipeline verification",
        RequestedValidTo = FixedNow.AddDays(14),
        Status = status,
        Version = version,
        TenantId = new TenantId(tenant),
        WorkflowMode = mode,
        ExternalSystemId = mode == ConsentWorkflowMode.External ? ExternalSystemId : null,
        ExternalReference = mode == ConsentWorkflowMode.External ? "CHG-42" : null,
        ApprovalSteps = steps ?? []
    };

    private static (
        ConsentDecisionPipeline Pipeline,
        IConsentApprovalRepository Approvals,
        ITableMetadataRepository Metadata,
        IDataOwnershipRepository Ownership,
        IConsentDecisionGuard Guard,
        IConsentWorkflowConfigRepository ConfigRepo,
        IScimUserStore ScimUsers,
        IScimGroupStore ScimGroups) BuildSut(bool tableFourEyes = false)
    {
        var approvals = Substitute.For<IConsentApprovalRepository>();
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(TableMeta(tableFourEyes));
        var ownership = Substitute.For<IDataOwnershipRepository>();
        ownership.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var guard = Substitute.For<IConsentDecisionGuard>();
        guard.Evaluate(Arg.Any<ClaimsPrincipal>()).Returns(ConsentDecisionVerdict.Allow());
        var grantPolicy = new GrantPolicy(Options.Create(new IndividualGrantOptions()));
        var validityPolicy = GrantValidityPolicy.CreateDefault();
        var configRepo = Substitute.For<IConsentWorkflowConfigRepository>();
        var scimUsers = Substitute.For<IScimUserStore>();
        var scimGroups = Substitute.For<IScimGroupStore>();
        scimGroups.GetEffectiveGroupSidsForUserAsync(
                Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EffectiveScimGroupsResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false));

        var externalGuard = new ExternalDecisionGuard(scimUsers, scimGroups);
        var clock = new FakeTimeProvider(FixedNow);

        var pipeline = new ConsentDecisionPipeline(
            approvals,
            metadata,
            ownership,
            guard,
            grantPolicy,
            validityPolicy,
            externalGuard,
            workflowConfigRepository: configRepo,
            groupStore: scimGroups,
            timeProvider: clock);

        return (pipeline, approvals, metadata, ownership, guard, configRepo, scimUsers, scimGroups);
    }

    [Fact]
    public async Task DecideAsync_InternalCallerRejectedByGuard_ThrowsDecisionNotPermitted_BeforeLoadingRequest()
    {
        var (pipeline, approvals, _, _, guard, _, _, _) = BuildSut();
        guard.Evaluate(Arg.Any<ClaimsPrincipal>()).Returns(ConsentDecisionVerdict.Deny("app_token"));

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(new ConsentDecisionCommand(
            Guid.NewGuid(), ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver1Sid))));

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.DecisionNotPermitted);
        await approvals.DidNotReceiveWithAnyArgs().GetConsentRequestAsync(default, default);
    }

    [Fact]
    public async Task DecideAsync_CrossTenantOrMissingRequest_ReturnsUniformRequestNotFound()
    {
        var (pipeline, approvals, _, _, _, _, _, _) = BuildSut();
        var foreignReq = Request(tenant: "tenant-b");
        var missingId = Guid.NewGuid();
        approvals.GetConsentRequestAsync(foreignReq.Id, Arg.Any<CancellationToken>()).Returns(foreignReq);
        approvals.GetConsentRequestAsync(missingId, Arg.Any<CancellationToken>()).Returns((ConsentRequest?)null);

        var foreignEx = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(
            new ConsentDecisionCommand(foreignReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver1Sid, "tenant-a"))));
        var missingEx = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(
            new ConsentDecisionCommand(missingId, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver1Sid, "tenant-a"))));

        foreignEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.RequestNotFound);
        missingEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.RequestNotFound);
    }

    [Fact]
    public async Task DecideAsync_ExpectedVersionMismatch_ReturnsPreconditionFailed()
    {
        var (pipeline, approvals, _, _, _, _, _, _) = BuildSut();
        var req = Request(version: 3);
        approvals.GetConsentRequestAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(
            new ConsentDecisionCommand(req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver1Sid), ExpectedVersion: 2)));

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.PreconditionFailed);
        ex.StatusCode.ShouldBe(412);
    }

    [Fact]
    public async Task DecideAsync_ChannelWorkflowMismatch_ReturnsWorkflowModeMismatch()
    {
        var (pipeline, approvals, _, _, _, _, _, _) = BuildSut();
        var externalReq = Request(mode: ConsentWorkflowMode.External, status: "PENDING_EXTERNAL_APPROVAL");
        approvals.GetConsentRequestAsync(externalReq.Id, Arg.Any<CancellationToken>()).Returns(externalReq);

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(
            new ConsentDecisionCommand(externalReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver1Sid))));

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.WorkflowModeMismatch);
    }

    [Fact]
    public async Task DecideAsync_InternalSingleStep_ActivatesAtomicConsent_WithShortenedValidTo()
    {
        var (pipeline, approvals, _, _, _, _, _, _) = BuildSut(tableFourEyes: false);
        var req = Request();
        approvals.GetConsentRequestAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        Consent? capturedConsent = null;
        approvals.ExecuteAtomicApprovalWithConsentAsync(
                req.Id, Arg.Any<ApprovalStep>(), Arg.Do<Consent>(c => capturedConsent = c),
                Arg.Any<IReadOnlyList<ConsentRowFilter>>(), Arg.Any<IReadOnlyList<ConsentColumnRule>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Consent>(2));

        var shortenedTo = FixedNow.AddDays(5);
        const string maskedColumns = """[{"columnName":"id","accessLevel":"Mask"},{"columnName":"amount","accessLevel":"Mask"}]""";
        var result = await pipeline.DecideAsync(new ConsentDecisionCommand(
            req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver1Sid),
            ColumnMaskingJson: maskedColumns, ApprovedValidTo: shortenedTo));

        result.Request.Status.ShouldBe("APPROVED");
        result.ActivatedConsent.ShouldNotBeNull();
        capturedConsent!.ValidTo.ShouldBe(shortenedTo);
        capturedConsent.CreatedBySid.ShouldBe(Approver1Sid);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("\" \"")]
    [InlineData("\"\"")]
    [InlineData("\"Agent.Read,Report.Read\"")]
    public async Task DecideAsync_ColumnMaskingWithBlankOrCommaScopeEntry_IsRejected_AndCreatesNoConsent(string scopeJson)
    {
        // Issue #806 review: a blank scope entry is stored as no scope, so the Clear rule would apply to every scope instead of one.
        var (pipeline, approvals, _, _, _, _, _, _) = BuildSut(tableFourEyes: false);
        var req = Request();
        approvals.GetConsentRequestAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        var masking = "[{\"columnName\":\"id\",\"accessLevel\":\"Mask\"},{\"columnName\":\"amount\",\"accessLevel\":\"Clear\",\"applicableScopes\":[" + scopeJson + "]}]";

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(new ConsentDecisionCommand(
            req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver1Sid), ColumnMaskingJson: masking)));

        ex.ErrorCode.ShouldBe("BAD_REQUEST");
        ex.StatusCode.ShouldBe(400);
        await approvals.DidNotReceiveWithAnyArgs().ExecuteAtomicApprovalWithConsentAsync(default, default!, default!, default!, default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DecideAsync_SensitiveColumnTableWithoutFourEyesFlag_SingleApproval_CreatesTheConsentAtomically_NeverLeavesApprovedRequestWithoutConsent()
    {
        // A table with a sensitive column classifies a user grant as GrantClass.Sensitive, but the repositories (authoritative under the row
        // lock) only require a second approver for highly sensitive tables. If the pipeline routed this through the step-only path the request
        // would become APPROVED without any consent row, so the requester would stay denied.
        var (pipeline, approvals, metadata, _, _, _, _, _) = BuildSut(tableFourEyes: false);
        var plain = TableMeta(fourEyes: false, sensitivity: "NORMAL");
        var meta = new TableMetadata
        {
            Identifier = plain.Identifier,
            Table = plain.Table,
            Columns =
            [
                plain.Columns[0],
                new TableColumn { Id = Guid.NewGuid(), TableId = plain.Table.Id, ColumnName = "email", DataType = "VARCHAR" }
            ]
        }.WithPii("email");
        metadata.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(meta);
        var req = Request();
        approvals.GetConsentRequestAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        approvals.ApproveConsentRequestStepAsync(req.Id, Approver1Sid, true, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                req.Status = "PENDING_SECOND_APPROVAL";
                req.ApprovalSteps.Add(new ApprovalStep { ConsentRequestId = req.Id, ApproverSid = Approver1Sid, Decision = "APPROVED" });
                return req;
            });
        approvals.ExecuteAtomicApprovalWithConsentAsync(
                req.Id, Arg.Any<ApprovalStep>(), Arg.Any<Consent>(),
                Arg.Any<IReadOnlyList<ConsentRowFilter>>(), Arg.Any<IReadOnlyList<ConsentColumnRule>>(), true, Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Consent>(2));

        var step1 = await pipeline.DecideAsync(new ConsentDecisionCommand(
            req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver1Sid)));

        step1.Request.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        step1.ActivatedConsent.ShouldBeNull();

        var step2 = await pipeline.DecideAsync(new ConsentDecisionCommand(
            req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver2Sid)));

        step2.Request.Status.ShouldBe("APPROVED");
        step2.ActivatedConsent.ShouldNotBeNull();
    }

    [Fact]
    public async Task DecideAsync_FourEyes_FirstStepAdvancesStatus_SecondSameApproverRefused_DistinctApproverCompletes()
    {
        var (pipeline, approvals, _, _, _, _, _, _) = BuildSut(tableFourEyes: true);
        var req = Request();
        approvals.GetConsentRequestAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        approvals.ApproveConsentRequestStepAsync(req.Id, Approver1Sid, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                req.Status = "PENDING_SECOND_APPROVAL";
                req.ApprovalSteps.Add(new ApprovalStep { ConsentRequestId = req.Id, ApproverSid = Approver1Sid, Decision = "APPROVED" });
                return req;
            });

        var step1 = await pipeline.DecideAsync(new ConsentDecisionCommand(
            req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver1Sid)));
        step1.Request.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        step1.ActivatedConsent.ShouldBeNull();

        var sameEx = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(
            new ConsentDecisionCommand(req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver1Sid))));
        sameEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);

        approvals.ExecuteAtomicApprovalWithConsentAsync(
                req.Id, Arg.Any<ApprovalStep>(), Arg.Any<Consent>(),
                Arg.Any<IReadOnlyList<ConsentRowFilter>>(), Arg.Any<IReadOnlyList<ConsentColumnRule>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Consent>(2));

        var step2 = await pipeline.DecideAsync(new ConsentDecisionCommand(
            req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver2Sid)));
        step2.Request.Status.ShouldBe("APPROVED");
        step2.ActivatedConsent.ShouldNotBeNull();
    }

    [Fact]
    public async Task DecideAsync_InternalTargetGroupMember_RefusedWithSelfApprovalForbidden()
    {
        var (pipeline, approvals, _, _, _, _, _, scimGroups) = BuildSut();
        var req = Request(granteeType: GranteeType.Group, granteeRef: "S-1-5-21-TARGET-GRP");
        approvals.GetConsentRequestAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        scimGroups.GetEffectiveGroupSidsForUserAsync(TenantA, Arg.Any<string>(), Approver1Sid.Value, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EffectiveScimGroupsResult(new HashSet<string>(["S-1-5-21-TARGET-GRP"]), false));

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(
            new ConsentDecisionCommand(req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver1Sid))));

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Self_approval_of_product_grant_rejected()
    {
        // the product owner is the data owner of the product object (an authorized approver), yet never decides their own request
        var product = Autheris.Application.Products.ProductSource.ObjectOf(TenantA, "sales", "orders_view");
        var (pipeline, approvals, _, ownership, _, _, _, _) = BuildSut();
        ownership.IsAuthorizedApproverForTableAsync(product, Approver1Sid, Arg.Any<CancellationToken>()).Returns(true);
        var req = Request(requester: Approver1Sid, table: product);
        approvals.GetConsentRequestAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => pipeline.DecideAsync(
            new ConsentDecisionCommand(req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Principal(Approver1Sid))));

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);
    }

    [Fact]
    public async Task DecideAsync_ExternalSingleStepAndReject_UseExternalRepositoryMethods()
    {
        var (pipeline, approvals, _, _, _, configRepo, scimUsers, _) = BuildSut(tableFourEyes: false);
        var req = Request(mode: ConsentWorkflowMode.External, status: "PENDING_EXTERNAL_APPROVAL");
        approvals.GetConsentRequestAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        configRepo.ResolveWorkflowRouteAsync(TenantA, req.TableIdentifier, Arg.Any<CancellationToken>())
            .Returns(new ConsentWorkflowRoute(TenantA.Value, WorkflowRouteScopeKind.Tenant, TenantA.Value, ConsentWorkflowMode.External, ExternalSystemId, 60, 30, 72, true, "admin", FixedNow));
        configRepo.GetExternalWorkflowSystemAsync(TenantA, ExternalSystemId, Arg.Any<CancellationToken>())
            .Returns(new ExternalWorkflowSystem(ExternalSystemId, TenantA.Value, "SNOW", "snow-client", "https://snow.local/hook", "sec", true, "admin", FixedNow, null));
        scimUsers.GetUserByObjectSidAsync(TenantA, Arg.Any<string>(), Approver1Sid.Value, Arg.Any<CancellationToken>())
            .Returns(new ScimUser("u-1", "ext-1", "alice", "Alice", "alice@corp.local", true, null, FixedNow, FixedNow, ObjectSid: Approver1Sid.Value));

        approvals.ExecuteExternalAtomicApprovalWithConsentAsync(
                req.Id, Arg.Any<ExternalDecisionAttribution>(), Arg.Any<ApprovalStep>(), Arg.Any<Consent>(),
                Arg.Any<IReadOnlyList<ConsentRowFilter>>(), Arg.Any<IReadOnlyList<ConsentColumnRule>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Consent>(3));

        const string maskedColumns = """[{"columnName":"id","accessLevel":"Mask"},{"columnName":"amount","accessLevel":"Mask"}]""";
        var extAttr = new ExternalDecisionAttribution(
            ExternalSystemId, "CHG-42", "APPROVED",
            new AssertedApprover(Approver1Sid.Value, "alice@corp.local"), FixedNow);
        var approved = await pipeline.DecideAsync(new ConsentDecisionCommand(
            req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.External, null,
            ColumnMaskingJson: maskedColumns, ExternalAttribution: extAttr));
        approved.ActivatedConsent.ShouldNotBeNull();
        approved.Request.Status.ShouldBe("APPROVED");

        req.Status = "PENDING_EXTERNAL_APPROVAL";
        var rejectedClone = Request(mode: ConsentWorkflowMode.External, status: "REJECTED");
        approvals.RejectExternalConsentRequestAsync(
                req.Id, Arg.Any<ExternalDecisionAttribution>(), Approver1Sid, "Policy denied", Arg.Any<CancellationToken>())
            .Returns(rejectedClone);
        var rejected = await pipeline.DecideAsync(new ConsentDecisionCommand(
            req.Id, ConsentDecisionKind.Reject, ConsentDecisionChannel.External, null, Reason: "Policy denied", ExternalAttribution: extAttr with { Decision = "REJECTED" }));
        rejected.Request.Status.ShouldBe("REJECTED");
    }
}
