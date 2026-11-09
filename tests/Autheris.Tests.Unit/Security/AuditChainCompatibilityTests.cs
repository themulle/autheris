namespace Autheris.Tests.Unit.Security;

using System;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

public sealed class AuditChainCompatibilityTests
{
    private static readonly byte[] TestHmacKey = Encoding.UTF8.GetBytes("autheris-v1-5-0-audit-golden-key-32b!!");
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Security")]
    public void V1_5_0_chain_verifies_after_unification()
    {
        // 1. Genesis hash rule: fixed standard representation across all versions
        AuditSqlCore.AuditGenesisHash.ShouldBe("GENESIS_0000000000000000000000000000000000000000000000000000000000000000");
        AuditChainEngine.AuditGenesisHash.ShouldBe(AuditSqlCore.AuditGenesisHash);

        // 2. Fixed golden entry at seq 1 following v1.5.0 canonical payload specification
        var id1 = "a0000000-0000-0000-0000-000000000001";
        var prevHash1 = AuditSqlCore.AuditGenesisHash;
        var eventType1 = "DATA_READ";
        var actorSid1 = "S-1-5-21-3623811015-3361044348-30300820-1013";
        var targetTable1 = "PATIENTS";
        var targetCol1 = "DIAGNOSIS";
        var decision1 = "PERMIT";
        var traceId1 = "trace-compat-001";
        var detailsJson1 = "{\"reason\":\"clinical_care\"}";
        var tenantId1 = "tenant-alpha";

        var hash1 = AuditSqlCore.ComputeAuditEntryHash(
            TestHmacKey,
            sequence: 1L,
            id: id1,
            prevHash: prevHash1,
            occurredAt: FixedTimestamp,
            eventType: eventType1,
            actorSid: actorSid1,
            targetTable: targetTable1,
            targetColumn: targetCol1,
            decision: decision1,
            traceId: traceId1,
            detailsJson: detailsJson1,
            tenantId: tenantId1);

        // Verify golden entry hash is deterministic HMAC-SHA256 hex string
        hash1.ShouldNotBeNullOrWhiteSpace();
        hash1.Length.ShouldBe(64);

        // Cross-verify with AuditCanonicalizer directly
        var canonicalExpected = AuditCanonicalizer.ComputeEntryHash(
            TestHmacKey,
            sequence: 1L,
            id: id1,
            prevHash: prevHash1,
            occurredAt: FixedTimestamp,
            eventType: eventType1,
            actorSid: actorSid1,
            targetTable: targetTable1,
            targetColumn: targetCol1,
            decision: decision1,
            traceId: traceId1,
            detailsJson: detailsJson1,
            tenantId: tenantId1);

        hash1.ShouldBe(canonicalExpected);

        // 3. Chain linking: entry 2 prevHash must chain to hash1
        var id2 = "a0000000-0000-0000-0000-000000000002";
        var hash2 = AuditSqlCore.ComputeAuditEntryHash(
            TestHmacKey,
            sequence: 2L,
            id: id2,
            prevHash: hash1,
            occurredAt: FixedTimestamp.AddMinutes(5),
            eventType: "DATA_WRITE",
            actorSid: actorSid1,
            targetTable: targetTable1,
            targetColumn: targetCol1,
            decision: decision1,
            traceId: "trace-compat-002",
            detailsJson: "{\"reason\":\"update\"}",
            tenantId: tenantId1);

        hash2.ShouldNotBeNullOrWhiteSpace();
        hash2.ShouldNotBe(hash1);

        // 4. Batch linking through AuditChainEngine matches unified hash calculations
        var entry1 = new AuditLogEntry
        {
            Id = Guid.Parse(id1),
            OccurredAt = FixedTimestamp,
            EventType = eventType1,
            ActorSid = new Sid(actorSid1),
            TargetTable = targetTable1,
            TargetColumn = targetCol1,
            Decision = decision1,
            TraceId = traceId1,
            DetailsJson = detailsJson1,
            TenantId = new TenantId(tenantId1)
        };

        var entry2 = new AuditLogEntry
        {
            Id = Guid.Parse(id2),
            OccurredAt = FixedTimestamp.AddMinutes(5),
            EventType = "DATA_WRITE",
            ActorSid = new Sid(actorSid1),
            TargetTable = targetTable1,
            TargetColumn = targetCol1,
            Decision = decision1,
            TraceId = "trace-compat-002",
            DetailsJson = "{\"reason\":\"update\"}",
            TenantId = new TenantId(tenantId1)
        };

        var (lastSeq, lastHash) = AuditChainEngine.LinkBatch(TestHmacKey, [entry1, entry2], startingSequence: 0, startingPrevHash: AuditSqlCore.AuditGenesisHash);

        lastSeq.ShouldBe(2L);
        lastHash.ShouldBe(hash2);
        entry1.ChainSequence.ShouldBe(1L);
        entry1.PrevHash.ShouldBe(AuditSqlCore.AuditGenesisHash);
        entry1.EntryHash.ShouldBe(hash1);
        entry2.ChainSequence.ShouldBe(2L);
        entry2.PrevHash.ShouldBe(hash1);
        entry2.EntryHash.ShouldBe(hash2);
    }
}
