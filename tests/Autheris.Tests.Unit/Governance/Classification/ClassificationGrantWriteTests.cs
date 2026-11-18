namespace Autheris.Tests.Unit.Governance.Classification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.ClassificationClear;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Identity;
using Autheris.Application.Policy.Classification;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Tenancy;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ClassificationGrantWriteTests
{
    private const string StewardGroup = "S-1-GRP-STEWARD";
    private const string HrGroup = "S-1-GRP-HR";
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid Requester = new("S-1-REQUESTER");
    private static readonly Sid Owner1 = new("S-1-OWNER-1");
    private static readonly Sid Owner2 = new("S-1-OWNER-2");
    private static readonly Sid Steward = new("S-1-STEWARD");
    private static readonly Sid HrSteward = new("S-1-HR-STEWARD");
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static RequestedClearItem Pii() => new("PII", ["email"]);
    private static RequestedClearItem PiiSpecial() => new("PII_SPECIAL", ["diagnosis"]);
    private static RequestedClearItem HrNote() => new("HR_NOTE", ["hr_note"]);

    private static ClaimsPrincipal Person(Sid sid) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, sid.Value), new Claim("tenant_id", TenantA.Value)],
        "Bearer", ClaimTypes.Name, ClaimTypes.Role));

    private static ColumnClassification Assigned(string code) => new()
    {
        Scope = ClassificationScopes.Global,
        TableColumnId = "x",
        Code = code,
        Source = "ADMIN"
    };

    private static TableMetadata Table()
    {
        var id = Guid.NewGuid();
        TableColumn Col(string name) => new() { Id = Guid.NewGuid(), TableId = id, ColumnName = name, DataType = "varchar" };
        return new TableMetadata
        {
            Identifier = OrdersTable,
            OwnerTenantId = TenantA.Value,
            Table = new Table { Id = id, SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
            Columns = [Col("id"), Col("email"), Col("diagnosis"), Col("token"), Col("hr_note")],
            ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = [Assigned("PII")],
                ["diagnosis"] = [Assigned("PII_SPECIAL")],
                ["token"] = [Assigned("CREDENTIALS")],
                ["hr_note"] = [Assigned("HR_NOTE")]
            }
        };
    }

    private static TestInMemoryClassificationStore Vocabulary(int? piiMaxClearDays = null)
    {
        var store = new TestInMemoryClassificationStore(TenantA.Value);
        store.Definitions["PII"] = store.Definitions["PII"] with { MaxClearDays = piiMaxClearDays };
        store.Definitions["PII_SPECIAL"] = store.Definitions["PII_SPECIAL"] with { StewardGroupSid = StewardGroup, MaxClearDays = 90 };
        store.Definitions["HR_NOTE"] = new DataClassification
        {
            Scope = TenantA.Value,
            Code = "HR_NOTE",
            DisplayName = "HR note",
            RestrictionRank = 50,
            ApproverMode = ApproverMode.Steward,
            StewardGroupSid = HrGroup,
            MaxClearDays = 90
        };
        return store;
    }

    private sealed class Harness
    {
        public required ConsentDecisionPipeline Pipeline { get; init; }
        public required IConsentApprovalRepository Approvals { get; init; }
        public required TestInMemoryClassificationStore Store { get; init; }
        public Consent? Activated { get; set; }
        public IReadOnlyList<ConsentColumnRule> ActivatedRules { get; set; } = [];
    }

    private static Harness Build(
        TestInMemoryClassificationStore? store = null,
        int? defaultMaxClearDays = null)
    {
        store ??= Vocabulary();
        var approvals = Substitute.For<IConsentApprovalRepository>();
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Table());

        var ownership = Substitute.For<IDataOwnershipRepository>();
        ownership.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Sid>(1) == Owner1 || call.ArgAt<Sid>(1) == Owner2);

        var guard = Substitute.For<IConsentDecisionGuard>();
        guard.Evaluate(Arg.Any<ClaimsPrincipal>()).Returns(ConsentDecisionVerdict.Allow());

        var groups = Substitute.For<IScimGroupStore>();
        groups.GetEffectiveGroupSidsForUserAsync(
                Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => new EffectiveScimGroupsResult(
                call.ArgAt<string>(2) switch
                {
                    "S-1-STEWARD" => new HashSet<string>([StewardGroup], StringComparer.OrdinalIgnoreCase),
                    "S-1-HR-STEWARD" => new HashSet<string>([HrGroup], StringComparer.OrdinalIgnoreCase),
                    _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                },
                false));

        var grantOptions = new GrantValidityOptions { DefaultMaxClearDays = defaultMaxClearDays ?? 90 };
        var harness = new Harness
        {
            Approvals = approvals,
            Store = store,
            Pipeline = new ConsentDecisionPipeline(
                approvals, metadata, ownership, guard,
                new GrantPolicy(Options.Create(new IndividualGrantOptions())),
                GrantValidityPolicy.CreateDefault(),
                new ExternalDecisionGuard(Substitute.For<IScimUserStore>(), groups),
                workflowConfigRepository: Substitute.For<IConsentWorkflowConfigRepository>(),
                groupStore: groups,
                timeProvider: new FakeTimeProvider(Now),
                classificationRepository: store,
                grantOptions: Options.Create(grantOptions))
        };

        Consent Capture(Consent c, IReadOnlyList<ConsentColumnRule> rules)
        {
            harness.Activated = c;
            harness.ActivatedRules = rules;
            return c;
        }

        approvals.ExecuteAtomicApprovalWithConsentAsync(
                Arg.Any<Guid>(), Arg.Any<ApprovalStep>(), Arg.Any<Consent>(),
                Arg.Any<IReadOnlyList<ConsentRowFilter>>(), Arg.Any<IReadOnlyList<ConsentColumnRule>>(), Arg.Any<CancellationToken>())
            .Returns(call => Capture(call.ArgAt<Consent>(2), call.ArgAt<IReadOnlyList<ConsentColumnRule>>(4)));
        approvals.ExecuteAtomicApprovalWithConsentAsync(
                Arg.Any<Guid>(), Arg.Any<ApprovalStep>(), Arg.Any<Consent>(),
                Arg.Any<IReadOnlyList<ConsentRowFilter>>(), Arg.Any<IReadOnlyList<ConsentColumnRule>>(), true, Arg.Any<CancellationToken>())
            .Returns(call => Capture(call.ArgAt<Consent>(2), call.ArgAt<IReadOnlyList<ConsentColumnRule>>(4)));
        approvals.ApproveConsentRequestStepAsync(Arg.Any<Guid>(), Arg.Any<Sid>(), true, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var req = approvals.GetConsentRequestAsync(call.ArgAt<Guid>(0), CancellationToken.None).GetAwaiter().GetResult()!;
                req.Status = "PENDING_SECOND_APPROVAL";
                return req;
            });
        return harness;
    }

    private static ConsentRequest Request(
        IReadOnlyList<RequestedClearItem> clear,
        string status = "PENDING",
        IEnumerable<Sid>? approvedBy = null,
        DateTimeOffset? validTo = null) => new()
    {
        Id = Guid.NewGuid(),
        TableId = Guid.NewGuid(),
        TableIdentifier = OrdersTable,
        RequesterSid = Requester,
        RequestedGranteeType = GranteeType.User,
        RequestedGranteeRef = Requester.Value,
        BusinessJustification = "Pseudonymised analytics need clear PII",
        RequestedValidTo = validTo ?? Now.AddDays(14),
        Status = status,
        TenantId = TenantA,
        ContextJson = RequestedClearCodec.Merge(null, clear),
        ApprovalSteps = (approvedBy ?? []).Select((sid, i) => new ApprovalStep { StepNumber = i + 1, ApproverSid = sid, Decision = "APPROVED" }).ToList()
    };

    private static Task<ConsentDecisionResult> Approve(
        Harness h, ConsentRequest req, Sid approver, IReadOnlyList<string>? approvedClear = null)
    {
        h.Approvals.GetConsentRequestAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        return h.Pipeline.DecideAsync(new ConsentDecisionCommand(
            req.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Person(approver),
            ApprovedClearClassifications: approvedClear));
    }

    private static bool ConsentWasCreated(Harness h) =>
        h.Approvals.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IConsentApprovalRepository.ExecuteAtomicApprovalWithConsentAsync));

    [Fact]
    public async Task Clear_on_credentials_rejected()
    {
        // Request side: the vocabulary rule rejects CREDENTIALS before anything is stored.
        var store = Vocabulary();
        var catalog = ClassificationClearPolicy.ResolveCatalog(TenantA.Value, await store.GetClassificationsAsync(TenantA.Value, true));
        var rejected = Should.Throw<ClearRequestException>(() => ClassificationClearPolicy.ValidateRequest(
            [new RequestedClearItem("CREDENTIALS", ["token"])], catalog, Table(), TenantA.Value));
        rejected.Code.ShouldBe(ClearRequestErrorCodes.NotGrantable);

        var created = new List<ConsentRequest>();
        var approvalRepo = Substitute.For<IConsentApprovalRepository>();
        approvalRepo.CreateConsentRequestAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => { created.Add(call.ArgAt<ConsentRequest>(0)); return call.ArgAt<ConsentRequest>(0); });
        var meta = Substitute.For<ITableMetadataRepository>();
        meta.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Table());
        var service = new ConsentRequestService(meta, approvalRepo, classificationRepository: store);

        var submission = new ConsentRequestSubmission(
            "sales", "dbo", "orders", "Need token column in clear", 7, null, Requester, Person(Requester), TenantA,
            RequestedClear: [new RequestedClearItem("CREDENTIALS", ["token"])]);
        var ex = await Should.ThrowAsync<ConsentRequestException>(() => service.SubmitAsync(submission));
        ex.Code.ShouldBe(ClearRequestErrorCodes.NotGrantable);
        created.ShouldBeEmpty();

        // Decision side: a stored request that names CREDENTIALS is never approved with Clear.
        var h = Build(store);
        var req = Request([new RequestedClearItem("CREDENTIALS", ["token"])]);
        var decide = await Should.ThrowAsync<ConsentDecisionException>(() => Approve(h, req, Owner1));
        decide.ErrorCode.ShouldBe(ClearRequestErrorCodes.NotGrantable);
        ConsentWasCreated(h).ShouldBeFalse();
        h.Store.GrantAccess.ShouldBeEmpty();
    }

    [Fact]
    public async Task Clear_request_is_persisted_in_the_context_and_validated_against_the_table()
    {
        var store = Vocabulary();
        var created = new List<ConsentRequest>();
        var approvalRepo = Substitute.For<IConsentApprovalRepository>();
        approvalRepo.CreateConsentRequestAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => { created.Add(call.ArgAt<ConsentRequest>(0)); return call.ArgAt<ConsentRequest>(0); });
        var meta = Substitute.For<ITableMetadataRepository>();
        meta.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Table());
        var service = new ConsentRequestService(meta, approvalRepo, classificationRepository: store);

        ConsentRequestSubmission Submission(RequestedClearItem item) => new(
            "sales", "dbo", "orders", "Need e-mail in clear", 7, null, Requester, Person(Requester), TenantA, RequestedClear: [item]);

        await service.SubmitAsync(Submission(new RequestedClearItem("PII", ["EMAIL"])));
        var stored = RequestedClearCodec.Parse(created.ShouldHaveSingleItem().ContextJson);
        stored.ShouldHaveSingleItem().Classification.ShouldBe("PII");
        stored[0].Columns.ShouldBe(["email"]);

        // A column that does not carry the classification, or Clear for a child via its parent, is refused (no hierarchy inheritance).
        (await Should.ThrowAsync<ConsentRequestException>(() => service.SubmitAsync(Submission(new RequestedClearItem("PII", ["diagnosis"])))))
            .Code.ShouldBe(ClearRequestErrorCodes.ColumnNotClassified);
        (await Should.ThrowAsync<ConsentRequestException>(() => service.SubmitAsync(Submission(new RequestedClearItem("MISSING", ["email"])))))
            .Code.ShouldBe(ClearRequestErrorCodes.UnknownClassification);
        created.Count.ShouldBe(1);
    }

    [Fact]
    public void Clear_request_routes_to_steward_for_PII_SPECIAL()
    {
        var catalog = ClassificationClearPolicy.ResolveCatalog(
            TenantA.Value, Vocabulary().Definitions.Values.ToList());

        ClassificationClearPolicy.BuildPlan([Pii()], catalog).NeedsSteward.ShouldBeFalse();

        var special = ClassificationClearPolicy.BuildPlan([Pii(), PiiSpecial()], catalog);
        special.NeedsSteward.ShouldBeTrue();
        special.StewardGroupSids.ShouldBe([StewardGroup]);

        var hr = ClassificationClearPolicy.BuildPlan([HrNote()], catalog);
        hr.StewardGroupSids.ShouldBe([HrGroup]);
    }

    [Fact]
    public async Task Owner_only_classification_clear_is_granted_by_a_single_owner_approval()
    {
        var h = Build();
        var req = Request([Pii()]);

        var result = await Approve(h, req, Owner1);

        result.ActivatedConsent.ShouldNotBeNull();
        var access = h.Store.GrantAccess.ShouldHaveSingleItem();
        access.Code.ShouldBe("PII");
        access.AccessLevel.ShouldBe(ColumnAccessLevel.Clear);
        access.ApprovedBySid.ShouldBe(Owner1.Value);
        access.ConsentId.ShouldBe(result.ActivatedConsent!.Id);
        h.ActivatedRules.Single(r => r.ColumnName == "email").AccessLevel.ShouldBe(ColumnAccessLevel.Clear);
        h.ActivatedRules.Where(r => r.ColumnName is "diagnosis" or "token" or "hr_note").ShouldAllBe(r => r.AccessLevel != ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task Owner_and_steward_classification_needs_sequential_four_eyes_owner_first()
    {
        var h = Build();
        var req = Request([PiiSpecial()]);

        // The steward cannot open the approval: the first step is always the data owner.
        var early = await Should.ThrowAsync<ConsentDecisionException>(() => Approve(h, req, Steward));
        early.ErrorCode.ShouldBe(ClearRequestErrorCodes.ApproverOrder);

        // The owner records the first step; nothing is granted yet.
        var first = await Approve(h, req, Owner1);
        first.ActivatedConsent.ShouldBeNull();
        first.Request.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        h.Store.GrantAccess.ShouldBeEmpty();
        await h.Approvals.Received(1).ApproveConsentRequestStepAsync(req.Id, Owner1, true, Arg.Any<CancellationToken>());

        // A second owner is not a steward: the request stays open and nothing is created.
        req.ApprovalSteps.Add(new ApprovalStep { StepNumber = 1, ApproverSid = Owner1, Decision = "APPROVED" });
        var wrongSecond = await Should.ThrowAsync<ConsentDecisionException>(() => Approve(h, req, Owner2));
        wrongSecond.ErrorCode.ShouldBe(ClearRequestErrorCodes.StewardRequired);
        ConsentWasCreated(h).ShouldBeFalse();

        // The same owner twice is refused by four-eyes.
        var sameOwner = await Should.ThrowAsync<ConsentDecisionException>(() => Approve(h, req, Owner1));
        sameOwner.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);

        // The steward completes it; the Clear is written with the steward as approver.
        var final = await Approve(h, req, Steward);
        final.ActivatedConsent.ShouldNotBeNull();
        var access = h.Store.GrantAccess.ShouldHaveSingleItem();
        access.Code.ShouldBe("PII_SPECIAL");
        access.ApprovedBySid.ShouldBe(Steward.Value);
        h.ActivatedRules.Single(r => r.ColumnName == "diagnosis").AccessLevel.ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task Steward_only_classification_requires_a_steward_of_that_group()
    {
        var h = Build();
        var req = Request([HrNote()], approvedBy: [Owner1], status: "PENDING_SECOND_APPROVAL");

        // PII_SPECIAL steward is not the steward of the HR group.
        var wrongGroup = await Should.ThrowAsync<ConsentDecisionException>(() => Approve(h, req, Steward));
        wrongGroup.ErrorCode.ShouldBe("FORBIDDEN");

        var result = await Approve(h, req, HrSteward);
        result.ActivatedConsent.ShouldNotBeNull();
        h.Store.GrantAccess.ShouldHaveSingleItem().Code.ShouldBe("HR_NOTE");
    }

    [Fact]
    public async Task Requester_cannot_approve_own_classification_clear()
    {
        var h = Build();

        // Even when the requester holds owner capability or the steward group, neither step may be theirs.
        var asOwner = Request([Pii()]);
        var ownerEx = await Should.ThrowAsync<ConsentDecisionException>(() => Approve(h, asOwner, Requester));
        ownerEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);

        var asSteward = Request([PiiSpecial()], approvedBy: [Owner1], status: "PENDING_SECOND_APPROVAL");
        var stewardEx = await Should.ThrowAsync<ConsentDecisionException>(() => Approve(h, asSteward, Requester));
        stewardEx.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.SelfApprovalForbidden);

        ConsentWasCreated(h).ShouldBeFalse();
        h.Store.GrantAccess.ShouldBeEmpty();
    }

    [Fact]
    public async Task Clear_validity_capped_by_max_clear_days()
    {
        // Classification limit 10 days, grant 14 days: Clear ends first.
        var h = Build(Vocabulary(piiMaxClearDays: 10));
        var result = await Approve(h, Request([Pii()]), Owner1);
        h.Store.GrantAccess.ShouldHaveSingleItem().ValidUntil.ShouldBe(Now.AddDays(10));
        result.ActivatedConsent!.ValidTo.ShouldBe(Now.AddDays(14));

        // Grant shorter than the limit: Clear never outlives the grant.
        var h2 = Build(Vocabulary(piiMaxClearDays: 10));
        var req2 = Request([Pii()]);
        h2.Approvals.GetConsentRequestAsync(req2.Id, Arg.Any<CancellationToken>()).Returns(req2);
        await h2.Pipeline.DecideAsync(new ConsentDecisionCommand(
            req2.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, Person(Owner1), ApprovedValidTo: Now.AddDays(5)));
        h2.Store.GrantAccess.ShouldHaveSingleItem().ValidUntil.ShouldBe(Now.AddDays(5));

        // No classification limit: the configured default applies.
        var h3 = Build(Vocabulary(piiMaxClearDays: null), defaultMaxClearDays: 7);
        await Approve(h3, Request([Pii()]), Owner1);
        h3.Store.GrantAccess.ShouldHaveSingleItem().ValidUntil.ShouldBe(Now.AddDays(7));
    }

    [Fact]
    public async Task Approver_can_only_narrow_never_widen_the_requested_clear()
    {
        var h = Build();
        var req = Request([Pii()]);

        var widen = await Should.ThrowAsync<ConsentDecisionException>(() => Approve(h, req, Owner1, ["PII", "PII_SPECIAL"]));
        widen.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.DecisionExceedsRequest);
        ConsentWasCreated(h).ShouldBeFalse();

        var narrowed = await Approve(h, req, Owner1, []);
        narrowed.ActivatedConsent.ShouldNotBeNull();
        h.Store.GrantAccess.ShouldBeEmpty();
        h.ActivatedRules.Where(r => r.ColumnName is "email" or "diagnosis" or "token" or "hr_note").ShouldAllBe(r => r.AccessLevel != ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task External_decision_cannot_widen_classification_level()
    {
        var h = Build();
        var externalSystemId = Guid.NewGuid();
        var req = Request([Pii()], status: "PENDING_EXTERNAL_APPROVAL");
        var external = new ConsentRequest
        {
            Id = req.Id,
            TableId = req.TableId,
            TableIdentifier = req.TableIdentifier,
            RequesterSid = req.RequesterSid,
            RequestedGranteeRef = req.RequestedGranteeRef,
            BusinessJustification = req.BusinessJustification,
            RequestedValidTo = req.RequestedValidTo,
            Status = req.Status,
            TenantId = TenantA,
            ContextJson = req.ContextJson,
            WorkflowMode = ConsentWorkflowMode.External,
            ExternalSystemId = externalSystemId
        };
        h.Approvals.GetConsentRequestAsync(external.Id, Arg.Any<CancellationToken>()).Returns(external);

        // The widening check runs before the external guard consults the workflow route, so a widening selection fails first.
        var attribution = new ExternalDecisionAttribution(
            externalSystemId, "CHG-1", "APPROVED", new AssertedApprover(Owner1.Value, "owner@corp.local"), Now);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Pipeline.DecideAsync(new ConsentDecisionCommand(
            external.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.External, null,
            ExternalAttribution: attribution, ContextTenantId: TenantA,
            ApprovedClearClassifications: ["PII", "PII_SPECIAL"])));

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.DecisionExceedsRequest);
        h.Store.GrantAccess.ShouldBeEmpty();
    }

    [Fact]
    public void Narrow_keeps_requested_items_and_flags_widening()
    {
        var requested = new[] { Pii(), PiiSpecial() };

        ClassificationClearPolicy.Narrow(requested, null, out var exceedsNull).ShouldBe(requested);
        exceedsNull.ShouldBeFalse();

        ClassificationClearPolicy.Narrow(requested, ["PII"], out var exceedsSubset)!.Select(i => i.Classification).ShouldBe(["PII"]);
        exceedsSubset.ShouldBeFalse();

        ClassificationClearPolicy.Narrow(requested, ["PII", "CONFIDENTIAL"], out var exceeds).ShouldBeNull();
        exceeds.ShouldBeTrue();
    }
}
