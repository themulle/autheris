using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed class UnmaskGrantApplierTests
{
    [Fact]
    public async Task Writes_unmask_grant_only_with_no_write_or_data_grant_kind()
    {
        Consent? persisted = null;
        var writer = NullFake.Create<IGrantWriter>((m, args) =>
        {
            if (m.Name == nameof(IGrantWriter.InsertAndGrantAsync))
            {
                persisted = (Consent)args![1]!;
                return Task.FromResult(new GrantRecord(persisted.TenantId.Value, 42, Guid.NewGuid(), GrantRecordType.Issued, persisted.Id, persisted.ConsentRequestId,
                    persisted.GrantSource, null, "User", "user:alice", "sales.dbo.customers", "{}", persisted.ValidFrom, persisted.ValidTo, "user:alice", "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, GrantRecordChain.GenesisHash));
            }

            return NullFake.UseDefault;
        });

        var applier = new UnmaskGrantApplier(writer);
        var now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
        using var rawTx = new DummyDbTransaction();
        var tx = NullFake.Create<IGovernanceTransaction>((m, _) =>
            m.Name == "get_" + nameof(IGovernanceTransaction.Transaction) ? rawTx : NullFake.UseDefault);

        var req = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("sales", "dbo", "customers"),
            RequesterSid = new Sid("user:alice"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "user:alice",
            BusinessJustification = "PII unmask needed for fraud review.",
            TenantId = new TenantId("tenant-a"),
            RequestKind = ConsentRequestKind.Unmask,
            GrantKind = PermissionGrantKind.Unmask,
            TargetColumns = ["email", "tax_id"]
        };

        var refs = await applier.ApplyAsync(tx, new ApprovedPermission(
            req, new Sid("user:owner"), ApproverCapacity.DataOwner, SelfGrant: false, now, now, now.AddDays(30)));

        refs.Count.ShouldBe(1);
        refs[0].GrantKind.ShouldBe(PermissionGrantKind.Unmask);
        persisted.ShouldNotBeNull();
        persisted.GrantKind.ShouldBe(PermissionGrantKind.Unmask);
        persisted.ApproverCapacity.ShouldBe(ApproverCapacity.DataOwner);
        persisted.TargetColumns.ShouldBe(["email", "tax_id"]);
        persisted.WriteRules.ShouldBeEmpty();
    }

    private sealed class DummyDbTransaction : DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }
}
