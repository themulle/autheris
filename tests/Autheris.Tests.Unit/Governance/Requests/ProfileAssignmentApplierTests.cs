using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.ConsentRequests.ProfileAssignments;
using Autheris.Tests.Unit.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed class ProfileAssignmentApplierTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Guid ProfileId = Guid.NewGuid();
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

    [Theory]
    [InlineData(ApproverCapacity.ItAdmin, false)]
    [InlineData(ApproverCapacity.SelfGrant, false)]
    [InlineData(ApproverCapacity.Governance, true)]
    public async Task It_cannot_approve_or_self_grant_profile_assignment(ApproverCapacity capacity, bool selfGrant)
    {
        var applier = new ProfileAssignmentApplier(
            NullFake.Create<IAccessProfileRepository>(),
            NullFake.Create<ITableMetadataRepository>(),
            NullFake.Create<IGrantWriter>());

        var req = CreateRequest();
        using var rawTx = new DummyDbTransaction();
        var tx = NullFake.Create<IGovernanceTransaction>((m, _) =>
            m.Name == "get_" + nameof(IGovernanceTransaction.Transaction) ? rawTx : NullFake.UseDefault);

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => applier.ApplyAsync(
            tx, new ApprovedPermission(req, new Sid("user:it"), capacity, selfGrant, Now, Now, Now.AddDays(30))));
        ex.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task End_date_required()
    {
        var applier = new ProfileAssignmentApplier(
            NullFake.Create<IAccessProfileRepository>(),
            NullFake.Create<ITableMetadataRepository>(),
            NullFake.Create<IGrantWriter>());

        using var rawTx = new DummyDbTransaction();
        var tx = NullFake.Create<IGovernanceTransaction>((m, _) =>
            m.Name == "get_" + nameof(IGovernanceTransaction.Transaction) ? rawTx : NullFake.UseDefault);

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => applier.ApplyAsync(
            tx, new ApprovedPermission(CreateRequest(), new Sid("user:dg"), ApproverCapacity.Governance, false, Now, Now, DateTimeOffset.MaxValue)));
        ex.ErrorCode.ShouldBe(GrantErrorCodes.EndDateRequired);
    }

    [Fact]
    public async Task Dg_and_owner_materialize_finite_data_grants()
    {
        var profiles = NullFake.Create<IAccessProfileRepository>((m, _) =>
            m.Name == nameof(IAccessProfileRepository.GetProfileAsync)
                ? Task.FromResult<AccessProfile?>(new AccessProfile
                {
                    ProfileId = ProfileId.ToString("D"),
                    Name = "SalesAnalyst",
                    TenantId = TenantA,
                    TargetTables = [OrdersTable.ToString()]
                })
                : NullFake.UseDefault);

        var metadata = NullFake.Create<ITableMetadataRepository>((m, _) =>
            m.Name == nameof(ITableMetadataRepository.GetTableMetadataAsync)
                ? Task.FromResult<TableMetadata?>(new TableMetadata
                {
                    Identifier = OrdersTable,
                    Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true }
                })
                : NullFake.UseDefault);

        var inserted = new List<Consent>();
        var grantWriter = NullFake.Create<IGrantWriter>((m, args) =>
        {
            if (m.Name == nameof(IGrantWriter.InsertAndGrantAsync))
            {
                var c = (Consent)args![1]!;
                inserted.Add(c);
                return Task.FromResult(new GrantRecord(TenantA.Value, 1, Guid.NewGuid(), GrantRecordType.Issued, c.Id, c.ConsentRequestId,
                    ConsentGrantSource.AccessProfile, ProfileId, "User", "user:alice", OrdersTable.ToString(), "{}", c.ValidFrom, c.ValidTo, "user:alice", "{}", Now, Now, GrantRecordChain.GenesisHash));
            }

            return NullFake.UseDefault;
        });

        var applier = new ProfileAssignmentApplier(profiles, metadata, grantWriter);
        using var rawTx = new DummyDbTransaction();
        var tx = NullFake.Create<IGovernanceTransaction>((m, _) =>
            m.Name == "get_" + nameof(IGovernanceTransaction.Transaction) ? rawTx : NullFake.UseDefault);

        var refs = await applier.ApplyAsync(
            tx, new ApprovedPermission(CreateRequest(), new Sid("user:dg"), ApproverCapacity.Governance, false, Now, Now, Now.AddDays(90)));

        refs.Count.ShouldBe(1);
        inserted.ShouldHaveSingleItem();
        inserted[0].GrantKind.ShouldBe(PermissionGrantKind.Data);
        inserted[0].GrantSource.ShouldBe(ConsentGrantSource.AccessProfile);
        inserted[0].ApproverCapacity.ShouldBe(ApproverCapacity.Governance);
        inserted[0].ValidTo.ShouldBe(Now.AddDays(90));
    }

    [Fact]
    public async Task PatternNewOwnerHook_ExtendsApprovingOwners_WithNewSignedGrantRecord()
    {
        var fx = new ProfileAssignmentTests.Fixture();
        var writer = new GrantWriter(fx, fx.Signer, fx.Clock);
        var applier = new ProfileAssignmentApplier(fx, fx, writer, assignmentStore: fx, materializer: fx.Materializer);
        var decidedAt = fx.Clock.GetUtcNow();

        // The original assignment materializes one concrete grant and one pattern grant.
        var assignment = fx.SeedApprovedAssignment();
        await ApplyInTransactionAsync(fx, applier, new ApprovedPermission(
            assignment, new Sid("user:dg"), ApproverCapacity.Governance, false, decidedAt, decidedAt, decidedAt.AddDays(30)));
        var patternConsent = fx.Consents.Single(c => c.IsPatternGrant);
        var grantCount = fx.Consents.Count;

        // A new object matches the pattern; its owner is queued for approval by a system request.
        var newTable = ProfileAssignmentTests.Fixture.BuildTable("pg", "analytics", "events_v2", "LOW");
        fx.Tables.Add(newTable);
        fx.TableOwners[newTable.Identifier] = [ProfileAssignmentTests.OwnerC.Value];
        fx.Clock.Advance(TimeSpan.FromHours(1));
        await fx.Maintenance.HandlePatternNewOwnerAsync(patternConsent, newTable, [ProfileAssignmentTests.OwnerC.Value], fx.Clock.GetUtcNow());
        var extReq = fx.Requests.Single(r => r.Id != assignment.Id);

        var refs = await ApplyInTransactionAsync(fx, applier, new ApprovedPermission(
            extReq, new Sid("user:dg"), ApproverCapacity.Governance, false, fx.Clock.GetUtcNow(), fx.Clock.GetUtcNow(), patternConsent.ValidTo));

        // The existing grant is extended (no new profile grant) and the extension is a new signed record on its chain.
        refs.ShouldHaveSingleItem().GrantId.ShouldBe(patternConsent.Id);
        fx.Consents.Count.ShouldBe(grantCount);
        fx.Consents.Single(c => c.Id == patternConsent.Id).ApprovingOwners.ShouldContain(ProfileAssignmentTests.OwnerC.Value);
        fx.Records.Count(r => r.ConsentId == patternConsent.Id && r.RecordType == GrantRecordType.Issued).ShouldBe(2);
        (await new GrantChainVerifier(fx, fx.Signer).VerifyTenantChainAsync(ProfileAssignmentTests.Tenant.Value)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Caller_made_request_cannot_extend_a_pattern_grant_through_its_context()
    {
        var fx = new ProfileAssignmentTests.Fixture();
        var applier = new ProfileAssignmentApplier(fx, fx, new GrantWriter(fx, fx.Signer, fx.Clock), assignmentStore: fx, materializer: fx.Materializer);
        var decidedAt = fx.Clock.GetUtcNow();
        var assignment = fx.SeedApprovedAssignment();
        await ApplyInTransactionAsync(fx, applier, new ApprovedPermission(
            assignment, new Sid("user:dg"), ApproverCapacity.Governance, false, decidedAt, decidedAt, decidedAt.AddDays(30)));
        var patternConsent = fx.Consents.Single(c => c.IsPatternGrant);

        var forged = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TenantId = ProfileAssignmentTests.Tenant,
            RequesterSid = ProfileAssignmentTests.Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = ProfileAssignmentTests.Requester.Value,
            BusinessJustification = "Forged extension of an existing pattern grant.",
            RequestKind = ConsentRequestKind.ProfileAssignment,
            GrantKind = PermissionGrantKind.Data,
            ProfileId = fx.ProfileId,
            ContextJson = System.Text.Json.JsonSerializer.Serialize(new ProfileAssignmentRequestContext(
                fx.ProfileId, "x", MaskingPolicyMode.Default, [], [new ProfileAssignmentSnapshotItem(0, "pg.analytics.*", true, "pg", [])],
                IncludesFutureObjects: true, ExtendingConsentId: patternConsent.Id))
        };

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => ApplyInTransactionAsync(fx, applier, new ApprovedPermission(
            forged, new Sid("user:dg"), ApproverCapacity.Governance, false, decidedAt, decidedAt, decidedAt.AddDays(30))));
        ex.StatusCode.ShouldBe(422);
        fx.Consents.Single(c => c.Id == patternConsent.Id).ApprovingOwners.ShouldBeEmpty();
    }

    private static async Task<IReadOnlyList<GrantRef>> ApplyInTransactionAsync(
        ProfileAssignmentTests.Fixture fx, ProfileAssignmentApplier applier, ApprovedPermission approved)
    {
        await using var assignmentTx = await fx.BeginTransactionAsync();
        var tx = NullFake.Create<IGovernanceTransaction>((m, _) =>
            m.Name == "get_" + nameof(IGovernanceTransaction.Transaction) ? assignmentTx.Transaction : NullFake.UseDefault);
        return await applier.ApplyAsync(tx, approved);
    }

    private static ConsentRequest CreateRequest() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantA,
        RequesterSid = new Sid("user:alice"),
        RequestedGranteeType = GranteeType.User,
        RequestedGranteeRef = "user:alice",
        BusinessJustification = "Analyst onboarding for sales profile.",
        RequestKind = ConsentRequestKind.ProfileAssignment,
        GrantKind = PermissionGrantKind.Data,
        ProfileId = ProfileId
    };

    private sealed class DummyDbTransaction : DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }
}
