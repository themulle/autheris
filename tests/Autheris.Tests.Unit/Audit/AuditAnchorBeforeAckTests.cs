using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>WP-PF-04 / SEC-SC-08: Tier-A audit batches are acknowledged only after commit AND anchor; the HMAC chain stays intact.</summary>
[Trait("Category", "Security")]
public sealed class AuditAnchorBeforeAckTests : IDisposable
{
    private readonly SwitchableAnchorStore _anchors = new();
    private readonly Autheris.Infrastructure.Persistence.SqliteGovernanceRepository _repo;

    public AuditAnchorBeforeAckTests()
    {
        _repo = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), auditAnchorStore: _anchors);
    }

    public void Dispose() => _repo.Dispose();

    private sealed class SwitchableAnchorStore : IAuditChainAnchorStore
    {
        private AuditChainAnchor? _last;
        public bool FailSave { get; set; }
        public int Saves { get; private set; }
        public AuditChainAnchor? Load() => _last;

        public void Save(AuditChainAnchor anchor)
        {
            if (FailSave) throw new IOException("anchor store down");
            Saves++;
            _last = anchor;
        }
    }

    private static AuditLogEntry TierA(int i) => new()
    {
        TenantId = TenantId.LegacySingleTenant,
        EventType = AuditEventTypes.AuditConfigChanged,
        ActorSid = new Sid($"S-1-5-21-ADMIN-{i}"),
        TargetTable = "governance",
        Decision = "ALLOW",
        TraceId = $"trace-{i}"
    };

    private static AuditLogEntry TierB(int i) => new()
    {
        TenantId = TenantId.LegacySingleTenant,
        EventType = AuditEventTypes.TableQuery,
        ActorSid = new Sid($"S-1-5-21-USER-{i}"),
        TargetTable = "default.sales.orders",
        Decision = "ALLOW",
        TraceId = $"trace-b-{i}"
    };

    [Fact]
    public async Task SEC_SC_08_TierABatch_WhenAnchorCannotBeWritten_IsNotAcknowledged_ButChainStaysLinear()
    {
        await _repo.RecordAuditEventAsync(TierA(0));
        var savesBefore = _anchors.Saves;
        _anchors.FailSave = true;

        await Should.ThrowAsync<AuditUnavailableException>(() => _repo.RecordAuditEventsAsync([TierA(1), TierA(2)]));

        // The rows are committed and the next batch re-anchors the tail; the chain verifies.
        _anchors.FailSave = false;
        await _repo.RecordAuditEventAsync(TierA(3));
        (await _repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
        _anchors.Saves.ShouldBe(savesBefore + 1);
        _anchors.Load()!.Sequence.ShouldBe(4);
    }

    [Fact]
    public async Task SEC_SC_08_SingleTierAWrite_WhenAnchorCannotBeWritten_IsNotAcknowledged()
    {
        _anchors.FailSave = true;

        await Should.ThrowAsync<AuditUnavailableException>(() => _repo.RecordAuditEventAsync(TierA(1)));
    }

    [Fact]
    public async Task SEC_SC_08_TierBOnlyBatch_NeverFailsOnALateAnchor()
    {
        _anchors.FailSave = true;

        await _repo.RecordAuditEventsAsync([TierB(1), TierB(2)]);

        _anchors.FailSave = false;
        await _repo.RecordAuditEventAsync(TierA(3));
        (await _repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
        _anchors.Load()!.Sequence.ShouldBe(3);
    }

    [Fact]
    public async Task SEC_SC_08_GroupCommit_WritesOneAnchorPerBatch_AndKeepsTheHmacChainLinked()
    {
        var batch = Enumerable.Range(0, 5).Select(TierA).ToList();
        var savesBefore = _anchors.Saves;

        await _repo.RecordAuditEventsAsync(batch);

        _anchors.Saves.ShouldBe(savesBefore + 1);
        _anchors.Load()!.Sequence.ShouldBe(5);
        for (int i = 1; i < batch.Count; i++)
        {
            batch[i].PrevHash.ShouldBe(batch[i - 1].EntryHash);
        }

        (await _repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }
}
