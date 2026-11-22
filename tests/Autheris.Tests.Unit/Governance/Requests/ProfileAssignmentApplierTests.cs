using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
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
