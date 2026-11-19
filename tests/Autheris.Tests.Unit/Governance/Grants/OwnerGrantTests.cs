namespace Autheris.Tests.Unit.Governance.Grants;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class OwnerGrantTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly TableIdentifier OrdersTable = new("pg", "sales", "orders");

    [Fact]
    public async Task OwnerGrant_WritesOpenEndedClearReadGrant_ExemptFromMaxDuration()
    {
        var clock = new FakeTimeProvider(FixedNow);
        var store = new InMemoryOwnerGrantAndRecordStore();
        var signer = EcdsaGrantRecordSigner.CreateEphemeral("owner-test-key");
        var writer = new GrantWriter(store, signer, clock);
        var service = new OwnerGrantService(writer, store, clock);
        await using var tx = new FakeDbTransaction();

        var tableId = Guid.NewGuid();
        var ownerSid = new Sid("S-1-5-21-DATA-OWNER-1");
        var actorSid = new Sid("S-1-5-21-ADMIN-1");

        var consent = await service.GrantOwnerAsync(tx, new OwnerGrantAssignment(
            tableId, OrdersTable, "owner-1", ownerSid, "Owner", Tenant, actorSid));

        consent.GrantSource.ShouldBe(ConsentGrantSource.Owner);
        consent.ValidFrom.ShouldBe(FixedNow);
        consent.ValidTo.ShouldBe(DateTimeOffset.MaxValue);
        consent.EffectiveValidTo.ShouldBe(DateTimeOffset.MaxValue);
        consent.ColumnSetMode.ShouldBe(ColumnSetMode.AllNonHidden);
        consent.WriteRules.ShouldBeEmpty();
        consent.GranteeSid.ShouldBe(ownerSid);

        store.Consents.Count.ShouldBe(1);
        store.Records.Count.ShouldBe(1);
        var record = store.Records[0];
        record.RecordType.ShouldBe(GrantRecordType.Issued);
        record.GrantSource.ShouldBe(ConsentGrantSource.Owner);
        record.ValidUntil.ShouldBeNull();
        record.GranteeRef.ShouldBe(ownerSid.Value);
        record.ObjectId.ShouldBe(OrdersTable.ToString());
        store.EpochBumps.ShouldBe(1);
        store.Audits.ShouldContain(a => a.EventType == AuditEventTypes.ConsentGranted && a.DetailsJson.Contains("OWNER"));

        var verifier = new GrantChainVerifier(store, signer);
        var verification = await verifier.VerifyTenantChainAsync(Tenant.Value);
        verification.IsValid.ShouldBeTrue();
        verification.VerifiedCount.ShouldBe(1);
    }

    [Fact]
    public async Task OwnerGrant_Unassign_RevokesAndBumpsEpoch_AndKeepsIfOtherOwnershipRoleRemains()
    {
        var clock = new FakeTimeProvider(FixedNow);
        var store = new InMemoryOwnerGrantAndRecordStore();
        var signer = EcdsaGrantRecordSigner.CreateEphemeral("owner-test-key");
        var writer = new GrantWriter(store, signer, clock);
        var service = new OwnerGrantService(writer, store, clock);
        await using var tx = new FakeDbTransaction();

        var tableId = Guid.NewGuid();
        var ownerSid = new Sid("S-1-5-21-DATA-OWNER-2");
        var actorSid = new Sid("S-1-5-21-ADMIN-1");

        var consent = await service.GrantOwnerAsync(tx, new OwnerGrantAssignment(
            tableId, OrdersTable, "owner-2", ownerSid, "Owner", Tenant, actorSid));

        store.HasOwnership = true;
        var skipped = await service.RevokeOwnerAsync(tx, new OwnerGrantRemoval(
            tableId, OrdersTable, "owner-2", ownerSid, "Owner", Tenant, actorSid, "Secondary role still present"));
        skipped.ShouldBe(0);
        store.Consents[0].IsRevoked.ShouldBeFalse();

        store.HasOwnership = false;
        clock.Advance(TimeSpan.FromHours(2));
        var revoked = await service.RevokeOwnerAsync(tx, new OwnerGrantRemoval(
            tableId, OrdersTable, "owner-2", ownerSid, "Owner", Tenant, actorSid, "Unassigned from table"));
        revoked.ShouldBe(1);
        store.Consents[0].IsRevoked.ShouldBeTrue();
        store.Records.Count.ShouldBe(2);
        store.Records[1].RecordType.ShouldBe(GrantRecordType.Revoked);
        store.Records[1].ConsentId.ShouldBe(consent.Id);
        store.EpochBumps.ShouldBe(2);
        store.Audits.ShouldContain(a => a.EventType == AuditEventTypes.ConsentRevoked && a.DetailsJson.Contains("OWNER"));

        var verifier = new GrantChainVerifier(store, signer);
        var verification = await verifier.VerifyTenantChainAsync(Tenant.Value);
        verification.IsValid.ShouldBeTrue();
        verification.VerifiedCount.ShouldBe(2);
    }

    [Fact]
    public async Task OwnerGrant_SelfGrantAttempt_IsRejected_AndWritesNothing()
    {
        var clock = new FakeTimeProvider(FixedNow);
        var store = new InMemoryOwnerGrantAndRecordStore();
        var writer = new GrantWriter(store, EcdsaGrantRecordSigner.CreateEphemeral("owner-test-key"), clock);
        var service = new OwnerGrantService(writer, store, clock);
        await using var tx = new FakeDbTransaction();

        var selfSid = new Sid("S-1-5-21-SELF-ACTOR");
        var ex = await Should.ThrowAsync<OwnerGrantException>(() => service.GrantOwnerAsync(tx, new OwnerGrantAssignment(
            Guid.NewGuid(), OrdersTable, "owner-self", selfSid, "Owner", Tenant, selfSid, [selfSid.Value])));

        ex.Code.ShouldBe("SELF_GRANT_FORBIDDEN");
        store.Consents.ShouldBeEmpty();
        store.Records.ShouldBeEmpty();
        store.EpochBumps.ShouldBe(0);
    }

    [Fact]
    public async Task OwnerGrant_DbtIngestionStyleSync_IssuesOnAdd_AndRevokesOnReplaceRemoval()
    {
        var clock = new FakeTimeProvider(FixedNow);
        var store = new InMemoryOwnerGrantAndRecordStore();
        var writer = new GrantWriter(store, EcdsaGrantRecordSigner.CreateEphemeral("owner-test-key"), clock);
        var service = new OwnerGrantService(writer, store, clock);
        await using var tx = new FakeDbTransaction();

        var tableId = Guid.NewGuid();
        var aliceSid = new Sid("S-1-5-21-ALICE");
        var bobSid = new Sid("S-1-5-21-BOB");
        var pipelineActor = new Sid("svc-dbt-ingestion");

        await service.GrantOwnerAsync(tx, new OwnerGrantAssignment(tableId, OrdersTable, "o-alice", aliceSid, "Steward", Tenant, pipelineActor));
        await service.GrantOwnerAsync(tx, new OwnerGrantAssignment(tableId, OrdersTable, "o-bob", bobSid, "Owner", Tenant, pipelineActor));
        store.Consents.Count(c => !c.IsRevoked).ShouldBe(2);

        store.HasOwnership = false;
        clock.Advance(TimeSpan.FromMinutes(15));
        await service.RevokeOwnerAsync(tx, new OwnerGrantRemoval(tableId, OrdersTable, "o-alice", aliceSid, "Steward", Tenant, pipelineActor, "Replace import removed owner"));

        store.Consents.Single(c => c.GranteeSid == aliceSid).IsRevoked.ShouldBeTrue();
        store.Consents.Single(c => c.GranteeSid == bobSid).IsRevoked.ShouldBeFalse();
    }

    private sealed class InMemoryOwnerGrantAndRecordStore : IOwnerGrantStore, IGrantRecordStore
    {
        private long _seq;
        private string _hash = GrantRecordChain.GenesisHash;
        public List<Consent> Consents { get; } = [];
        public List<GrantRecord> Records { get; } = [];
        public List<AuditLogEntry> Audits { get; } = [];
        public int EpochBumps { get; private set; }
        public bool HasOwnership { get; set; }

        public Task<IReadOnlyList<Consent>> GetActiveOwnerGrantsAsync(DbTransaction transaction, Guid tableId, Sid ownerSid, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>(Consents.Where(c => c.TableId == tableId && !c.IsRevoked && c.GranteeSid == ownerSid).ToList());

        public Task<bool> HasActiveTableOwnershipAsync(DbTransaction transaction, Guid tableId, string ownerId, CancellationToken ct = default) =>
            Task.FromResult(HasOwnership);

        public Task MarkOwnerGrantRevokedAsync(DbTransaction transaction, Guid consentId, Sid actorSid, string reason, DateTimeOffset now, CancellationToken ct = default)
        {
            var idx = Consents.FindIndex(x => x.Id == consentId);
            var c = Consents[idx];
            Consents[idx] = CloneConsent(c, isRevoked: true, revokedBy: actorSid, revokedAt: now, revokeReason: reason);
            return Task.CompletedTask;
        }

        public Task IncrementTableEpochAsync(DbTransaction transaction, TableIdentifier table, CancellationToken ct = default)
        {
            EpochBumps++;
            return Task.CompletedTask;
        }

        public Task RecordAuditInTransactionAsync(DbTransaction transaction, AuditLogEntry entry, CancellationToken ct = default)
        {
            Audits.Add(entry);
            return Task.CompletedTask;
        }

        public Task<(long LastSeq, string LastHash)> LockAndReadHeadAsync(DbTransaction transaction, string tenantId, CancellationToken ct = default) =>
            Task.FromResult((_seq, _hash));

        public Task AppendRecordAndAdvanceHeadAsync(DbTransaction transaction, GrantRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            _seq = record.Seq;
            _hash = record.RecordHash;
            return Task.CompletedTask;
        }

        public Task UpdateConsentGrantRecordSeqAsync(DbTransaction transaction, string tenantId, Guid consentId, long seq, CancellationToken ct = default)
        {
            var idx = Consents.FindIndex(c => c.Id == consentId);
            if (idx >= 0)
            {
                Consents[idx] = CloneConsent(Consents[idx], grantRecordSeq: seq);
            }

            return Task.CompletedTask;
        }

        public Task InsertConsentRowAsync(DbTransaction transaction, Consent consent, long grantRecordSeq, CancellationToken ct = default)
        {
            Consents.Add(CloneConsent(consent, grantRecordSeq: grantRecordSeq));
            return Task.CompletedTask;
        }

        private static Consent CloneConsent(
            Consent c,
            bool? isRevoked = null,
            Sid? revokedBy = null,
            DateTimeOffset? revokedAt = null,
            string? revokeReason = null,
            long? grantRecordSeq = null) => new()
        {
            Id = c.Id,
            TableId = c.TableId,
            TableIdentifier = c.TableIdentifier,
            ConsentRequestId = c.ConsentRequestId,
            Effect = c.Effect,
            TenantId = c.TenantId,
            GranteeType = c.GranteeType,
            GranteeSid = c.GranteeSid,
            CreatedBySid = c.CreatedBySid,
            ValidFrom = c.ValidFrom,
            ValidTo = c.ValidTo,
            IsRevoked = isRevoked ?? c.IsRevoked,
            RevokedBySid = revokedBy ?? c.RevokedBySid,
            RevokedAt = revokedAt ?? c.RevokedAt,
            RevokeReason = revokeReason ?? c.RevokeReason,
            Description = c.Description,
            WriteRules = c.WriteRules,
            ColumnRules = c.ColumnRules,
            RowFilters = c.RowFilters,
            GrantSource = c.GrantSource,
            GrantRecordSeq = grantRecordSeq ?? c.GrantRecordSeq,
            ProfileAssignmentId = c.ProfileAssignmentId,
            ObjectPattern = c.ObjectPattern,
            DataSource = c.DataSource,
            ColumnSetMode = c.ColumnSetMode,
            WorkflowMode = c.WorkflowMode,
            ProfileItemNo = c.ProfileItemNo,
            ApprovingOwners = c.ApprovingOwners
        };

        public Task<IReadOnlyList<GrantRecord>> ReadTenantChainAsync(string tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GrantRecord>>(Records.Where(r => r.TenantId == tenantId).OrderBy(r => r.Seq).ToList());

        public Task<IReadOnlyList<GrantRecord>> ReadConsentChainAsync(string tenantId, Guid consentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GrantRecord>>(Records.Where(r => r.TenantId == tenantId && r.ConsentId == consentId).OrderBy(r => r.Seq).ToList());

        public Task<GrantRecord?> ReadRecordBySeqAsync(string tenantId, long seq, CancellationToken ct = default) =>
            Task.FromResult(Records.FirstOrDefault(r => r.TenantId == tenantId && r.Seq == seq));

        public Task<IReadOnlyList<GrantRecord>> ReadRecordRangeAsync(string tenantId, long fromSeq, long? toSeq, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GrantRecord>>(Records.Where(r => r.TenantId == tenantId && r.Seq >= fromSeq && (!toSeq.HasValue || r.Seq <= toSeq.Value)).OrderBy(r => r.Seq).ToList());
    }

    private sealed class FakeDbTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }
}
