namespace Autheris.Tests.Unit.ConsentRequests.ProfileAssignments;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class ProfileGrantMaterializerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid Requester = new("S-1-5-21-USER-ALICE");
    private static readonly Sid Owner = new("S-1-5-21-OWNER-A");

    [Fact]
    public async Task MaterializeApprovedAsync_FutureRequestedValidFrom_IsPreservedOnConsentAndGrantRecord()
    {
        var fx = new MaterializerFixture();
        var futureStart = FixedNow.AddDays(4);
        var validTo = FixedNow.AddDays(20);
        var req = fx.CreateRequest(futureStart, validTo);

        var consents = await fx.Materializer.MaterializeApprovedAsync(
            new DummyTransaction(),
            req,
            fx.Context,
            [new ProfileAssignmentItemRow(Tenant, req.Id, 0, "sales.dbo.orders", Owner.Value, "APPROVED")],
            [fx.Table],
            Owner,
            [],
            validTo,
            FixedNow,
            description: null,
            narrowedWriteRules: null,
            approvedItemNos: null,
            approvedClearColumns: null);

        var consent = consents.ShouldHaveSingleItem();
        consent.ValidFrom.ShouldBe(futureStart);
        consent.IsActive(FixedNow.AddDays(3)).ShouldBeFalse();
        consent.IsActive(futureStart).ShouldBeTrue();
        fx.LastGrantInput.ShouldNotBeNull();
        fx.LastGrantInput!.ValidFrom.ShouldBe(futureStart);
    }

    [Fact]
    public async Task MaterializeApprovedAsync_PastOrNullRequestedValidFrom_FallsBackToDecidedAt()
    {
        var fx = new MaterializerFixture();
        var validTo = FixedNow.AddDays(20);
        var req = fx.CreateRequest(FixedNow.AddHours(-2), validTo);

        var consents = await fx.Materializer.MaterializeApprovedAsync(
            new DummyTransaction(),
            req,
            fx.Context,
            [new ProfileAssignmentItemRow(Tenant, req.Id, 0, "sales.dbo.orders", Owner.Value, "APPROVED")],
            [fx.Table],
            Owner,
            [],
            validTo,
            FixedNow,
            description: null,
            narrowedWriteRules: null,
            approvedItemNos: null,
            approvedClearColumns: null);

        var consent = consents.ShouldHaveSingleItem();
        consent.ValidFrom.ShouldBe(FixedNow);
        fx.LastGrantInput!.ValidFrom.ShouldBe(FixedNow);
    }

    private sealed class MaterializerFixture : IGrantWriter, IProfileAssignmentStore
    {
        public TableMetadata Table { get; } = new()
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = "orders",
                IsActive = true
            },
            Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" }]
        };

        public ProfileAssignmentRequestContext Context { get; } = new(
            Guid.NewGuid(),
            "analytics-base",
            MaskingPolicyMode.Default,
            [],
            [new ProfileAssignmentSnapshotItem(0, "sales.dbo.orders", false, "sales", [])],
            false);

        public ProfileGrantMaterializer Materializer { get; }
        public GrantRecordInput? LastGrantInput { get; private set; }

        public MaterializerFixture()
        {
            Materializer = new ProfileGrantMaterializer(this, this, new FakeTimeProvider(FixedNow));
        }

        public ConsentRequest CreateRequest(DateTimeOffset? validFrom, DateTimeOffset validTo) => new()
        {
            Id = Guid.NewGuid(),
            TableId = Table.Table.Id,
            TableIdentifier = Table.Identifier,
            RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester.Value,
            BusinessJustification = "Profile validity window",
            RequestedValidFrom = validFrom,
            RequestedValidTo = validTo,
            TenantId = Tenant,
            RequestKind = ConsentRequestKind.ProfileAssignment,
            ProfileId = Context.ProfileId,
            Status = "PENDING"
        };

        public Task<GrantRecord> InsertAndGrantAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default)
        {
            LastGrantInput = input;
            return Task.FromResult(new GrantRecord(
                input.TenantId, 1, Guid.NewGuid(), GrantRecordType.Issued, consent.Id, input.RequestId,
                consent.GrantSource, consent.ProfileAssignmentId, input.GranteeType ?? "User",
                input.GranteeRef ?? Requester.Value, input.ObjectId ?? Table.Identifier.ToString(),
                input.ScopeJson, input.ValidFrom, input.ValidUntil, input.RequestedBy, input.DecisionJson,
                input.DecidedAt, input.DecidedAt, GrantRecordChain.GenesisHash));
        }

        public Task<GrantRecord> GrantAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRecord> RevokeAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRecord> RecordRejectionAsync(DbTransaction transaction, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRecord> RecordExpiryAsync(DbTransaction transaction, Consent? consent, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IProfileAssignmentTransaction> BeginTransactionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertAssignmentItemsAsync(DbTransaction? transaction, IReadOnlyList<ProfileAssignmentItemRow> items, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ProfileAssignmentItemRow>> GetAssignmentItemsAsync(DbTransaction? transaction, Guid requestId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProfileAssignmentItemRow>>([]);
        public Task<int> DecideAssignmentItemsAsync(DbTransaction transaction, Guid requestId, IReadOnlyCollection<string> targetOwnerSids, string decision, string decidedBy, DateTimeOffset decidedAt, bool decideAllPending, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<Consent>> GetConsentsByProfileAssignmentAsync(DbTransaction? transaction, TenantId tenantId, Guid profileAssignmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<Consent?> GetConsentByIdInTransactionAsync(DbTransaction transaction, Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task MarkConsentRevokedInTransactionAsync(DbTransaction transaction, Guid consentId, Sid actorSid, string reason, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddApprovingOwnersInTransactionAsync(DbTransaction transaction, Consent consent, IReadOnlyCollection<string> ownerSids, CancellationToken ct = default) => Task.CompletedTask;
        public Task IncrementTableEpochInTransactionAsync(DbTransaction transaction, TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAuditInTransactionAsync(DbTransaction transaction, AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task ApplyRequestStepAndStatusInTransactionAsync(DbTransaction transaction, ConsentRequest req, string? newStatus, Sid approverSid, string stepDecision, string? reason, DateTimeOffset decidedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task InvalidateTablesAfterCommitAsync(IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DummyTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }
}
