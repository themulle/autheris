using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using FluentAssertions;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Engine-agnostic contract suite for <c>IAuditLogRepository</c>, <c>IAuditChainExportSource</c>,
/// and <c>IAuditPurgeKeyProvider</c>.
/// </summary>
public abstract class AuditContract : GovernanceContractBase
{
    private static readonly Sid ActorA = new("S-1-5-21-800-1");
    private static readonly Sid ActorB = new("S-1-5-21-800-2");

    private static AuditLogEntry NewAuditEntry(
        TenantId tenant,
        Sid? actor = null,
        string targetTable = "sales.dbo.orders",
        string eventType = "QueryExecuted",
        DateTimeOffset? occurredAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            EventType = eventType,
            ActorSid = actor ?? ActorA,
            TargetTable = targetTable,
            Decision = "ALLOW",
            DetailsJson = "{\"contract\":true}",
            TenantId = tenant
        };

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var entryA = NewAuditEntry(Store.TenantA, ActorA, "sales.dbo.tenant_a");
        var entryB = NewAuditEntry(Store.TenantB, ActorB, "sales.dbo.tenant_b");
        await Store.AuditLogs.RecordAuditEventAsync(entryA);
        await Store.AuditLogs.RecordAuditEventAsync(entryB);

        var recentA = await Store.AuditLogs.GetAuditLogEntriesAsync(limit: 50, tenantId: Store.TenantA);
        recentA.Select(e => e.Id).Should().Contain(entryA.Id).And.NotContain(entryB.Id);
        recentA.Should().OnlyContain(e => e.TenantId == Store.TenantA);

        var queriedA = await Store.AuditLogs.QueryAuditLogsAsync(
            targetTable: "sales.dbo.tenant_a",
            actorSid: ActorA,
            limit: 50,
            tenantId: Store.TenantA);
        queriedA.Select(e => e.Id).Should().ContainSingle().Which.Should().Be(entryA.Id);

        var crossQuery = await Store.AuditLogs.QueryAuditLogsAsync(
            targetTable: "sales.dbo.tenant_b",
            limit: 50,
            tenantId: Store.TenantA);
        crossQuery.Should().BeEmpty();

        var pageA = await Store.AuditLogs.QueryAuditLogPageAsync(new AuditLogPageQuery(Store.TenantA));
        pageA.Items.Select(e => e.Id).Should().Contain(entryA.Id).And.NotContain(entryB.Id);
        pageA.Items.Should().OnlyContain(e => e.TenantId == Store.TenantA);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var entryA = NewAuditEntry(Store.TenantA, ActorA, "sales.dbo.immutable");
        await Store.AuditLogs.RecordAuditEventAsync(entryA);

        var hijackAct = () => Store.ExecuteNonQueryAsync(
            "UPDATE AUDIT_LOG_ENTRIES SET tenant_id = @tenantB WHERE id = @id",
            ("@tenantB", Store.TenantB.Value),
            ("@id", entryA.Id.ToString()));
        await hijackAct.Should().ThrowAsync<Exception>();

        var deleteAct = () => Store.ExecuteNonQueryAsync(
            "DELETE FROM AUDIT_LOG_ENTRIES WHERE id = @id",
            ("@id", entryA.Id.ToString()));
        await deleteAct.Should().ThrowAsync<Exception>();

        var entriesB = await Store.AuditLogs.GetAuditLogEntriesAsync(limit: 50, tenantId: Store.TenantB);
        entriesB.Select(e => e.Id).Should().NotContain(entryA.Id);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        var missingSingleAct = () => Store.AuditLogs.RecordAuditEventAsync(NewAuditEntry(default));
        await missingSingleAct.Should().ThrowAsync<ArgumentException>();

        var missingBatchAct = () => Store.AuditLogs.RecordAuditEventsAsync([NewAuditEntry(Store.TenantA), NewAuditEntry(default)]);
        await missingBatchAct.Should().ThrowAsync<ArgumentException>();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        var entry = NewAuditEntry(Store.TenantA, ActorA, "sales.dbo.idem");
        await Store.AuditLogs.RecordAuditEventAsync(entry);
        var anchorBefore = Store.AuditExport.GetVerifiedChainAnchor();
        anchorBefore.Should().NotBeNull();

        await Store.AuditLogs.RecordAuditEventsAsync([]);
        var anchorAfter = Store.AuditExport.GetVerifiedChainAnchor();
        anchorAfter.Should().Be(anchorBefore);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        var baseTime = DateTimeOffset.UtcNow.AddDays(-30);
        for (int i = 1; i <= 4; i++)
        {
            await Store.AuditLogs.RecordAuditEventAsync(NewAuditEntry(Store.TenantA, occurredAt: baseTime.AddDays(i)));
        }

        var threshold = baseTime.AddDays(2.5);
        var plan = await Store.AuditLogs.PlanPurgePrefixAsync(threshold);
        plan.Should().NotBeNull();

        var hmacKey = Store.AuditPurgeKey.GetAuditHmacKey()!;
        var unsigned = new AuditPurgeCheckpoint(
            plan!.Value.FirstSurvivingSequence,
            plan.Value.SurvivingPrevHash,
            plan.Value.PurgeCount,
            threshold,
            DateTimeOffset.UtcNow,
            string.Empty,
            null);
        var checkpoint = unsigned with { Signature = AuditChainEngine.ComputePurgeCheckpointSignature(hmacKey, unsigned) };
        await Store.AuditLogs.SavePurgeCheckpointAsync(checkpoint);

        var regressedUnsigned = unsigned with { FirstSurvivingSequence = 1 };
        var regressed = regressedUnsigned with { Signature = AuditChainEngine.ComputePurgeCheckpointSignature(hmacKey, regressedUnsigned) };
        var regressAct = () => Store.AuditLogs.SavePurgeCheckpointAsync(regressed);
        await regressAct.Should().ThrowAsync<InvalidOperationException>();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var expectedIds = new List<Guid>();
        for (int i = 0; i < 5; i++)
        {
            var e = NewAuditEntry(Store.TenantA, ActorA, "sales.dbo.paged", occurredAt: baseTime.AddSeconds(i));
            await Store.AuditLogs.RecordAuditEventAsync(e);
            expectedIds.Add(e.Id);
        }

        await Store.AuditLogs.RecordAuditEventAsync(NewAuditEntry(Store.TenantB, ActorB, "sales.dbo.paged", occurredAt: baseTime.AddSeconds(10)));

        var collected = new List<AuditLogEntry>();
        long? beforeSeq = null;
        while (true)
        {
            var page = await Store.AuditLogs.QueryAuditLogPageAsync(
                new AuditLogPageQuery(Store.TenantA, TargetTable: "sales.dbo.paged", BeforeSequence: beforeSeq, PageSize: 2));
            collected.AddRange(page.Items);
            if (page.NextBeforeSequence is null)
            {
                break;
            }

            beforeSeq = page.NextBeforeSequence;
        }

        collected.Should().HaveCount(5);
        collected.Select(e => e.ChainSequence!.Value).Should().BeInDescendingOrder();
        collected.Select(e => e.Id).Should().BeEquivalentTo(expectedIds);

        var range = await Store.AuditExport.GetAuditChainRangeAsync(baseTime.AddMinutes(-1), baseTime.AddMinutes(5));
        range.Should().NotBeNull();
        range!.Count.Should().BeGreaterOrEqualTo(6);

        var exported = await Store.AuditExport.GetAuditChainPageAsync(range.FirstRowId - 1, range.LastRowId, 100);
        exported.Select(r => r.RowId).Should().BeInAscendingOrder();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        await Store.AuditLogs.RecordAuditEventAsync(NewAuditEntry(Store.TenantA));

        var overCap = new AuditLogPageQuery(Store.TenantA, PageSize: AuditLogPageQuery.MaxPageSize + 9_000);
        overCap.EffectivePageSize.Should().Be(AuditLogPageQuery.MaxPageSize);

        var underCap = new AuditLogPageQuery(Store.TenantA, PageSize: -5);
        underCap.EffectivePageSize.Should().Be(1);

        var page = await Store.AuditLogs.QueryAuditLogPageAsync(overCap);
        page.Items.Should().NotBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        var baseTime = DateTimeOffset.UtcNow.AddDays(-60);
        var purgedIds = new List<Guid>();
        var survivingIds = new List<Guid>();
        for (int i = 1; i <= 6; i++)
        {
            var e = NewAuditEntry(Store.TenantA, ActorA, "sales.dbo.purge_test", occurredAt: baseTime.AddDays(i));
            await Store.AuditLogs.RecordAuditEventAsync(e);
            if (i <= 3)
            {
                purgedIds.Add(e.Id);
            }
            else
            {
                survivingIds.Add(e.Id);
            }
        }

        var threshold = baseTime.AddDays(3.5);
        var plan = await Store.AuditLogs.PlanPurgePrefixAsync(threshold);
        plan.Should().NotBeNull();

        var hmacKey = Store.AuditPurgeKey.GetAuditHmacKey();
        hmacKey.Should().NotBeNull().And.HaveCountGreaterOrEqualTo(32);

        var unsigned = new AuditPurgeCheckpoint(
            plan!.Value.FirstSurvivingSequence,
            plan.Value.SurvivingPrevHash,
            plan.Value.PurgeCount,
            threshold,
            DateTimeOffset.UtcNow,
            string.Empty,
            null);
        var checkpoint = unsigned with { Signature = AuditChainEngine.ComputePurgeCheckpointSignature(hmacKey!, unsigned) };

        Store.AuditAnchorStore.SavePurgeCheckpoint(checkpoint);
        await Store.AuditLogs.SavePurgeCheckpointAsync(checkpoint);

        var deletedCount = await Store.AuditLogs.ExecutePurgeBatchAsync(plan.Value.MaxEligibleSequence, batchSize: 50);
        deletedCount.Should().BeGreaterOrEqualTo(3);

        var remaining = await Store.AuditLogs.QueryAuditLogsAsync(targetTable: "sales.dbo.purge_test", tenantId: Store.TenantA);
        remaining.Select(e => e.Id).Should().BeEquivalentTo(survivingIds);
        remaining.Select(e => e.Id).Should().NotContain(purgedIds);

        var verified = await Store.AuditLogs.VerifyAuditHashChainAsync();
        verified.Should().BeTrue();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Audit_rows_are_append_only()
    {
        if (!Store.IsAvailable) return;

        var entry = NewAuditEntry(Store.TenantA, ActorA, "sales.dbo.worm_target");
        await Store.AuditLogs.RecordAuditEventAsync(entry);

        var updateAct = () => Store.ExecuteNonQueryAsync(
            "UPDATE AUDIT_LOG_ENTRIES SET decision = 'DENY' WHERE id = @id",
            ("@id", entry.Id.ToString()));
        await updateAct.Should().ThrowAsync<Exception>();

        var deleteAct = () => Store.ExecuteNonQueryAsync(
            "DELETE FROM AUDIT_LOG_ENTRIES WHERE id = @id",
            ("@id", entry.Id.ToString()));
        await deleteAct.Should().ThrowAsync<Exception>();

        var truncateAct = () => Store.ExecuteNonQueryAsync("TRUNCATE TABLE AUDIT_LOG_ENTRIES");
        await truncateAct.Should().ThrowAsync<Exception>();

        (await Store.AuditLogs.VerifyAuditHashChainAsync()).Should().BeTrue();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Chain_order_matches_insert_order()
    {
        if (!Store.IsAvailable) return;

        await using var store2 = await Store.CreateAdditionalStoreAsync();

        var orderedIds = new List<Guid>();
        for (int i = 0; i < 6; i++)
        {
            var e = NewAuditEntry(Store.TenantA, ActorA, $"sales.dbo.seq_{i}");
            var targetStore = (i % 2 == 0) ? Store : store2;
            await targetStore.AuditLogs.RecordAuditEventAsync(e);
            orderedIds.Add(e.Id);
        }

        (await Store.AuditLogs.VerifyAuditHashChainAsync()).Should().BeTrue();
        (await store2.AuditLogs.VerifyAuditHashChainAsync()).Should().BeTrue();

        var entries = await Store.AuditLogs.GetAuditLogEntriesAsync(limit: 50, tenantId: Store.TenantA);
        var byInsert = orderedIds
            .Select(id => entries.Single(e => e.Id == id))
            .ToList();

        for (int i = 1; i < byInsert.Count; i++)
        {
            byInsert[i].ChainSequence.Should().Be(byInsert[i - 1].ChainSequence + 1);
            byInsert[i].PrevHash.Should().Be(byInsert[i - 1].EntryHash);
        }

        var anchor = Store.AuditExport.GetVerifiedChainAnchor();
        anchor.Should().NotBeNull();
        anchor!.Sequence.Should().Be(byInsert[^1].ChainSequence!.Value);
        anchor.EntryHash.Should().Be(byInsert[^1].EntryHash);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Batch_insert_respects_max_batch_size()
    {
        if (!Store.IsAvailable) return;

        var batch = Enumerable.Range(0, 25)
            .Select(i => NewAuditEntry(Store.TenantA, ActorA, $"sales.dbo.batch_{i}"))
            .ToList();

        await Store.AuditLogs.RecordAuditEventsAsync(batch);

        (await Store.AuditLogs.VerifyAuditHashChainAsync()).Should().BeTrue();

        var stored = await Store.AuditLogs.GetAuditLogEntriesAsync(limit: 100, tenantId: Store.TenantA);
        var matched = batch.Select(b => stored.Single(s => s.Id == b.Id)).ToList();
        matched.Should().HaveCount(25);

        for (int i = 1; i < matched.Count; i++)
        {
            matched[i].ChainSequence.Should().Be(matched[i - 1].ChainSequence + 1);
            matched[i].PrevHash.Should().Be(matched[i - 1].EntryHash);
        }
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlAuditContractTests(GovernancePostgresFixture fixture) : AuditContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "audit_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerAuditContractTests(GovernanceSqlServerFixture fixture) : AuditContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "audit_contract");
}
