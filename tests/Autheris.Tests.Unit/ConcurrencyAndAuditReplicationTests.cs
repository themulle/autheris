using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class ConcurrencyAndAuditReplicationTests : IDisposable
{
    private readonly EpochValidationService _epochService;
    private readonly SqliteGovernanceRepository _repository;
    private readonly List<string> _tempFiles = new();

    public ConcurrencyAndAuditReplicationTests()
    {
        _epochService = new EpochValidationService();
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = $"Data Source=concurrency_audit_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            }
        });
        _repository = new SqliteGovernanceRepository(_epochService, options);
    }

    public void Dispose()
    {
        _repository.Dispose();
        // SEC H-17: file databases get an external audit chain anchor next to them.
        foreach (var file in _tempFiles.SelectMany(f => new[] { f, f + ".audit-anchor.json" }).ToList())
        {
            if (File.Exists(file))
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }
    }

    #region High-Concurrency Stress Tests

    [Fact]
    public async Task AuditLog_UnderHighConcurrency_MaintainsCryptographicChainWithoutGapsOrForks()
    {
        const int totalEvents = 60;
        var tasks = Enumerable.Range(1, totalEvents).Select(i => Task.Run(async () =>
        {
            var actorSid = new Sid($"S-1-5-21-WORKER-{i % 5}");
            await _repository.RecordAuditEventAsync(new AuditLogEntry
            {
                EventType = "CONCURRENT_DATA_ACCESS",
                ActorSid = actorSid,
                TargetTable = "finance.dbo.finance_table_1",
                Decision = i % 3 == 0 ? "DENY" : "ALLOW",
                TraceId = $"trace-stress-{i:D4}",
                DetailsJson = $"{{\"workerId\": {i}}}"
            });
        }));

        await Task.WhenAll(tasks);

        // Verify the entire hash chain
        var isChainValid = await _repository.VerifyAuditHashChainAsync();
        isChainValid.ShouldBeTrue("Audit hash chain must remain 100% valid under high concurrency.");

        var entries = await _repository.GetAuditLogEntriesAsync(limit: 200);
        entries.Count.ShouldBeGreaterThanOrEqualTo(totalEvents);

        // Ensure every entry is strictly chained
        for (int j = 1; j < entries.Count; j++)
        {
            entries[j].PrevHash.ShouldBe(entries[j - 1].EntryHash,
                $"Entry at index {j} has disconnected PrevHash.");
        }
    }

    [Fact]
    public async Task ConcurrentConsentCreations_OnSameTable_IncrementEpochAtomicallyWithoutLostUpdates()
    {
        var table = new TableIdentifier("sales", "dbo", "sales_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var initialEpoch = await _repository.GetTableEpochAsync(table);
        const int concurrentConsents = 12;

        var tasks = Enumerable.Range(1, concurrentConsents).Select(i => Task.Run(async () =>
        {
            var userSid = new Sid($"S-1-5-21-CONCURRENT-USER-{i}");
            var consent = new Consent
            {
                TableId = meta.Table.Id,
                TableIdentifier = table,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(14)
            };
            await _repository.CreateConsentAsync(consent);
        }));

        await Task.WhenAll(tasks);

        var finalEpoch = await _repository.GetTableEpochAsync(table);
        finalEpoch.ShouldBe(initialEpoch + concurrentConsents,
            "Each consent creation must atomically increment the table's policy epoch.");
    }

    #endregion

    #region Multi-Instance Replication & Restarts

    [Fact]
    public async Task AuditLog_AcrossThreeConsecutiveInstances_MaintainsUnbrokenChain()
    {
        var dbFile = $"audit_multi_inst_{Guid.NewGuid():N}.db";
        _tempFiles.Add(dbFile);
        var connStr = $"Data Source={dbFile}";
        var opts = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = connStr }
        });

        // Instance 1: records events 1..5
        using (var inst1 = new SqliteGovernanceRepository(_epochService, opts))
        {
            for (int i = 1; i <= 5; i++)
            {
                await inst1.RecordAuditEventAsync(new AuditLogEntry
                {
                    EventType = "INSTANCE_1_EVENT",
                    ActorSid = new Sid("S-1-5-21-INST-1"),
                    TargetTable = "crm.dbo.contacts",
                    Decision = "ALLOW",
                    TraceId = $"trace-inst1-{i}"
                });
            }
        }

        // Instance 2: records events 6..10
        using (var inst2 = new SqliteGovernanceRepository(_epochService, opts))
        {
            for (int i = 6; i <= 10; i++)
            {
                await inst2.RecordAuditEventAsync(new AuditLogEntry
                {
                    EventType = "INSTANCE_2_EVENT",
                    ActorSid = new Sid("S-1-5-21-INST-2"),
                    TargetTable = "crm.dbo.contacts",
                    Decision = "ALLOW",
                    TraceId = $"trace-inst2-{i}"
                });
            }
        }

        // Instance 3: records event 11, then verifies all 11 events
        using (var inst3 = new SqliteGovernanceRepository(_epochService, opts))
        {
            await inst3.RecordAuditEventAsync(new AuditLogEntry
            {
                EventType = "INSTANCE_3_EVENT",
                ActorSid = new Sid("S-1-5-21-INST-3"),
                TargetTable = "crm.dbo.contacts",
                Decision = "ALLOW",
                TraceId = "trace-inst3-11"
            });

            var entries = await inst3.GetAuditLogEntriesAsync(50);
            entries.Count.ShouldBe(11);

            var isValid = await inst3.VerifyAuditHashChainAsync();
            isValid.ShouldBeTrue("Chain across 3 consecutive repository lifetimes must remain completely valid.");
        }
    }

    [Fact]
    public async Task AuditLog_TamperDetection_DetectsPayloadAlteringAtAnyPosition()
    {
        var dbFile = $"audit_tamper_test_{Guid.NewGuid():N}.db";
        _tempFiles.Add(dbFile);
        var connStr = $"Data Source={dbFile}";
        var opts = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = connStr }
        });

        Guid middleEventId = Guid.Empty;
        using (var repo = new SqliteGovernanceRepository(_epochService, opts))
        {
            // Seed 5 events
            for (int i = 1; i <= 5; i++)
            {
                var id = Guid.NewGuid();
                if (i == 3) middleEventId = id;

                await repo.RecordAuditEventAsync(new AuditLogEntry
                {
                    Id = id,
                    EventType = $"EVENT_{i}",
                    ActorSid = new Sid($"S-1-5-21-ACTOR-{i}"),
                    TargetTable = "hr.dbo.salaries",
                    Decision = "ALLOW",
                    TraceId = $"trace-tamper-{i}",
                    DetailsJson = $"{{\"record\": {i}}}"
                });
            }

            var initiallyValid = await repo.VerifyAuditHashChainAsync();
            initiallyValid.ShouldBeTrue();

            // 1. Tamper with DetailsJson of middle event
            using (var cmd = repo.Connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE AUDIT_LOG_ENTRIES SET details_json = '{\"tampered\": true}' WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", middleEventId.ToString());
                await cmd.ExecuteNonQueryAsync();
            }

            // Verification must immediately catch this!
            var isTamperDetected = await repo.VerifyAuditHashChainAsync();
            isTamperDetected.ShouldBeFalse("Tampered payload in details_json must be detected by SHA-256 verification.");
        }
    }

    [Fact]
    public async Task AuditLog_DeletionOfMiddleEntry_BreaksHashChain()
    {
        var dbFile = $"audit_delete_test_{Guid.NewGuid():N}.db";
        _tempFiles.Add(dbFile);
        var connStr = $"Data Source={dbFile}";
        var opts = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = connStr }
        });

        Guid middleEventId = Guid.Empty;
        using (var repo = new SqliteGovernanceRepository(_epochService, opts))
        {
            for (int i = 1; i <= 5; i++)
            {
                var id = Guid.NewGuid();
                if (i == 3) middleEventId = id;

                await repo.RecordAuditEventAsync(new AuditLogEntry
                {
                    Id = id,
                    EventType = $"AUDIT_DELETE_EVENT_{i}",
                    ActorSid = new Sid($"S-1-5-21-USER-{i}"),
                    TargetTable = "finance.dbo.ledger",
                    Decision = "ALLOW",
                    TraceId = $"trace-del-{i}"
                });
            }

            // Delete the 3rd event directly
            using (var cmd = repo.Connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM AUDIT_LOG_ENTRIES WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", middleEventId.ToString());
                await cmd.ExecuteNonQueryAsync();
            }

            // The chain is now broken because entry 4's PrevHash matches the deleted entry 3's EntryHash, not entry 2's EntryHash
            var isValid = await repo.VerifyAuditHashChainAsync();
            isValid.ShouldBeFalse("Deletion of a middle audit log entry must break the PrevHash linkage.");
        }
    }

    [Fact]
    public async Task AuditLog_GdprAnonymization_DoesNotBreakAuditHashChain()
    {
        var actorSid = new Sid("S-1-5-21-GDPR-SUBJECT");

        // 1. Audit event records only actor_sid
        await _repository.RecordAuditEventAsync(new AuditLogEntry
        {
            EventType = "GDPR_TEST_EVENT",
            ActorSid = actorSid,
            TargetTable = "finance.dbo.finance_table_1",
            Decision = "ALLOW",
            TraceId = "trace-gdpr-001"
        });

        var isValidBefore = await _repository.VerifyAuditHashChainAsync();
        isValidBefore.ShouldBeTrue();

        // 2. Anonymize user in DataOwners or external directory
        using (var cmd = _repository.Connection.CreateCommand())
        {
            cmd.CommandText = @"UPDATE DATA_OWNERS
                                SET display_name = 'Anonymized User', email = 'anonymized@corp.local'
                                WHERE ad_sid = @sid";
            cmd.Parameters.AddWithValue("@sid", actorSid.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // 3. Hash chain remains 100% intact because personal identity details are decoupled from the hash payload
        var isValidAfter = await _repository.VerifyAuditHashChainAsync();
        isValidAfter.ShouldBeTrue("GDPR anonymization of actor directory must not break the cryptographic audit hash chain.");
    }

    #endregion
}
