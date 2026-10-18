using System;
using System.IO;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>
/// SEC-AUD161-02: Cryptographic continuity and verification tests across unpurged,
/// single-purged, and multi-purged audit logs, including tamper and deletion detection.
/// </summary>
[Trait("Category", "Security")]
public sealed class AuditPurgeChainIntegrityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-purge-chain-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbFile;
    private readonly string _anchorFile;

    public AuditPurgeChainIntegrityTests()
    {
        Directory.CreateDirectory(_dir);
        _dbFile = Path.Combine(_dir, "audit.db");
        _anchorFile = Path.Combine(_dir, "audit.anchor.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private (SqliteGovernanceRepository Repo, FileAuditChainAnchorStore Store) CreateRepository()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source={_dbFile}", SeedDemoData = false },
            Audit = new AuditOptions { ChainAnchorPath = _anchorFile }
        });

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");

        var store = new FileAuditChainAnchorStore(_anchorFile);
        var repo = GovernanceStoreFactory.CreateSqlite(
            new EpochValidationService(),
            options,
            env,
            auditAnchorStore: store);

        return (repo, store);
    }

    private static AuditLogEntry CreateEntry(int index, DateTimeOffset occurredAt) => new()
    {
        Id = Guid.NewGuid(),
        EventType = AuditEventTypes.AuthSucceeded,
        ActorSid = new Sid($"S-1-5-21-{index}"),
        TargetTable = "dbo.Users",
        Decision = "ALLOW",
        TraceId = $"trace-{index}",
        DetailsJson = "{}",
        OccurredAt = occurredAt
    };

    [Fact]
    public async Task VerifyAuditHashChain_UnpurgedLog_ReturnsTrue()
    {
        var (repo, _) = CreateRepository();
        using (repo)
        {
            var baseTime = DateTimeOffset.UtcNow.AddDays(-10);
            for (int i = 1; i <= 10; i++)
            {
                await repo.RecordAuditEventAsync(CreateEntry(i, baseTime.AddHours(i)));
            }

            var verified = await repo.VerifyAuditHashChainAsync();
            verified.ShouldBeTrue("Unpurged hash chain should verify successfully.");
            repo.IsAuditPipelineFaulted.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task VerifyAuditHashChain_AfterPurgeWithCheckpoint_ReturnsTrue()
    {
        var (repo, store) = CreateRepository();
        using (repo)
        {
            var baseTime = DateTimeOffset.UtcNow.AddDays(-100);
            for (int i = 1; i <= 10; i++)
            {
                await repo.RecordAuditEventAsync(CreateEntry(i, baseTime.AddDays(i)));
            }

            // Entries 1..5 are older than threshold
            var thresholdUtc = baseTime.AddDays(5.5);
            var plan = await repo.PlanPurgePrefixAsync(thresholdUtc);
            plan.ShouldNotBeNull();
            var (maxEligibleSeq, firstSurvivingSeq, survivingPrevHash, purgeCount) = plan.Value;
            firstSurvivingSeq.ShouldBe(6);

            // Compute valid HMAC signature
            var hmacKey = ((IAuditPurgeKeyProvider)repo).GetAuditHmacKey()!;
            var now = DateTimeOffset.UtcNow;
            var unsigned = new AuditPurgeCheckpoint(
                firstSurvivingSeq,
                survivingPrevHash,
                purgeCount,
                thresholdUtc,
                now,
                string.Empty,
                null);

            var sig = AuditChainEngine.ComputePurgeCheckpointSignature(hmacKey, unsigned);
            var checkpoint = unsigned with { Signature = sig };

            // Save to external store and repo
            store.SavePurgeCheckpoint(checkpoint);
            await repo.SavePurgeCheckpointAsync(checkpoint);

            // Execute purge batch
            var purged = await repo.ExecutePurgeBatchAsync(maxEligibleSeq, 100);
            purged.ShouldBe(5);

            // Verification must pass starting from checkpoint sequence
            var verified = await repo.VerifyAuditHashChainAsync();
            verified.ShouldBeTrue("Audit hash chain should verify successfully from purge checkpoint.");
            repo.IsAuditPipelineFaulted.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task VerifyAuditHashChain_MultipleConsecutivePurges_ReturnsTrue()
    {
        var (repo, store) = CreateRepository();
        using (repo)
        {
            var baseTime = DateTimeOffset.UtcNow.AddDays(-200);
            for (int i = 1; i <= 20; i++)
            {
                await repo.RecordAuditEventAsync(CreateEntry(i, baseTime.AddDays(i)));
            }

            var hmacKey = ((IAuditPurgeKeyProvider)repo).GetAuditHmacKey()!;

            // First purge: 5 entries
            var threshold1 = baseTime.AddDays(5.5);
            var plan1 = await repo.PlanPurgePrefixAsync(threshold1);
            plan1.ShouldNotBeNull();
            var cp1Unsigned = new AuditPurgeCheckpoint(plan1.Value.FirstSurvivingSequence, plan1.Value.SurvivingPrevHash, plan1.Value.PurgeCount, threshold1, DateTimeOffset.UtcNow, string.Empty, null);
            var cp1 = cp1Unsigned with { Signature = AuditChainEngine.ComputePurgeCheckpointSignature(hmacKey, cp1Unsigned) };
            store.SavePurgeCheckpoint(cp1);
            await repo.SavePurgeCheckpointAsync(cp1);
            await repo.ExecutePurgeBatchAsync(plan1.Value.MaxEligibleSequence, 100);

            (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();

            // Second purge: 5 more entries (seq 6..10)
            var threshold2 = baseTime.AddDays(10.5);
            var plan2 = await repo.PlanPurgePrefixAsync(threshold2);
            plan2.ShouldNotBeNull();
            plan2.Value.FirstSurvivingSequence.ShouldBe(11);
            var cp2Unsigned = new AuditPurgeCheckpoint(plan2.Value.FirstSurvivingSequence, plan2.Value.SurvivingPrevHash, plan2.Value.PurgeCount, threshold2, DateTimeOffset.UtcNow, string.Empty, null);
            var cp2 = cp2Unsigned with { Signature = AuditChainEngine.ComputePurgeCheckpointSignature(hmacKey, cp2Unsigned) };
            store.SavePurgeCheckpoint(cp2);
            await repo.SavePurgeCheckpointAsync(cp2);
            await repo.ExecutePurgeBatchAsync(plan2.Value.MaxEligibleSequence, 100);

            (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue("Multi-purge hash chain should verify successfully.");
        }
    }

    [Fact]
    public async Task VerifyAuditHashChain_TamperedPostPurgeEntry_ReturnsFalse()
    {
        var (repo, store) = CreateRepository();
        using (repo)
        {
            var baseTime = DateTimeOffset.UtcNow.AddDays(-100);
            for (int i = 1; i <= 10; i++)
            {
                await repo.RecordAuditEventAsync(CreateEntry(i, baseTime.AddDays(i)));
            }

            var thresholdUtc = baseTime.AddDays(5.5);
            var plan = await repo.PlanPurgePrefixAsync(thresholdUtc);
            var hmacKey = ((IAuditPurgeKeyProvider)repo).GetAuditHmacKey()!;
            var unsigned = new AuditPurgeCheckpoint(plan!.Value.FirstSurvivingSequence, plan.Value.SurvivingPrevHash, plan.Value.PurgeCount, thresholdUtc, DateTimeOffset.UtcNow, string.Empty, null);
            var checkpoint = unsigned with { Signature = AuditChainEngine.ComputePurgeCheckpointSignature(hmacKey, unsigned) };
            store.SavePurgeCheckpoint(checkpoint);
            await repo.SavePurgeCheckpointAsync(checkpoint);
            await repo.ExecutePurgeBatchAsync(plan.Value.MaxEligibleSequence, 100);

            // Tamper entry at seq 7
            using (var conn = new SqliteConnection($"Data Source={_dbFile}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE AUDIT_LOG_ENTRIES SET decision = 'DENY' WHERE seq = 7";
                cmd.ExecuteNonQuery();
            }

            var verified = await repo.VerifyAuditHashChainAsync();
            verified.ShouldBeFalse("Tampered post-purge entry must fail chain verification.");
            repo.IsAuditPipelineFaulted.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task VerifyAuditHashChain_MissingEntrySequenceHole_ReturnsFalse()
    {
        var (repo, store) = CreateRepository();
        using (repo)
        {
            var baseTime = DateTimeOffset.UtcNow.AddDays(-100);
            for (int i = 1; i <= 10; i++)
            {
                await repo.RecordAuditEventAsync(CreateEntry(i, baseTime.AddDays(i)));
            }

            var thresholdUtc = baseTime.AddDays(5.5);
            var plan = await repo.PlanPurgePrefixAsync(thresholdUtc);
            var hmacKey = ((IAuditPurgeKeyProvider)repo).GetAuditHmacKey()!;
            var unsigned = new AuditPurgeCheckpoint(plan!.Value.FirstSurvivingSequence, plan.Value.SurvivingPrevHash, plan.Value.PurgeCount, thresholdUtc, DateTimeOffset.UtcNow, string.Empty, null);
            var checkpoint = unsigned with { Signature = AuditChainEngine.ComputePurgeCheckpointSignature(hmacKey, unsigned) };
            store.SavePurgeCheckpoint(checkpoint);
            await repo.SavePurgeCheckpointAsync(checkpoint);
            await repo.ExecutePurgeBatchAsync(plan.Value.MaxEligibleSequence, 100);

            // Unauthorized deletion of seq 8 creating a gap
            using (var conn = new SqliteConnection($"Data Source={_dbFile}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM AUDIT_LOG_ENTRIES WHERE seq = 8";
                cmd.ExecuteNonQuery();
            }

            var verified = await repo.VerifyAuditHashChainAsync();
            verified.ShouldBeFalse("Sequence hole post-purge must fail chain verification.");
            repo.IsAuditPipelineFaulted.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task VerifyAuditHashChain_TamperedCheckpointSignature_ReturnsFalse()
    {
        var (repo, store) = CreateRepository();
        using (repo)
        {
            var baseTime = DateTimeOffset.UtcNow.AddDays(-100);
            for (int i = 1; i <= 10; i++)
            {
                await repo.RecordAuditEventAsync(CreateEntry(i, baseTime.AddDays(i)));
            }

            var thresholdUtc = baseTime.AddDays(5.5);
            var plan = await repo.PlanPurgePrefixAsync(thresholdUtc);

            // Bad signature on checkpoint
            var badCheckpoint = new AuditPurgeCheckpoint(
                plan!.Value.FirstSurvivingSequence,
                plan.Value.SurvivingPrevHash,
                plan.Value.PurgeCount,
                thresholdUtc,
                DateTimeOffset.UtcNow,
                "BAD_TAMPERED_HMAC_SIGNATURE_HEX",
                null);

            store.SavePurgeCheckpoint(badCheckpoint);
            await repo.SavePurgeCheckpointAsync(badCheckpoint);
            await repo.ExecutePurgeBatchAsync(plan.Value.MaxEligibleSequence, 100);

            var verified = await repo.VerifyAuditHashChainAsync();
            verified.ShouldBeFalse("Tampered purge checkpoint signature must fail chain verification.");
            repo.IsAuditPipelineFaulted.ShouldBeTrue();
        }
    }
}
