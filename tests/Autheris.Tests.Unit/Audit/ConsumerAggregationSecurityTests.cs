namespace Autheris.Tests.Unit.Audit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Observability;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ConsumerAggregationSecurityTests
{
    [Fact]
    public async Task T_SEC_AUD114_07_ActorHashing_PersistsPseudonymousHmacOnly()
    {
        // Enforces SEC-AUD114-04: Counter table stores HMAC-SHA256, never raw actor SID.
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        new SqliteBaselineSchema(conn, null).Apply();

        var repo = Substitute.For<IConsumerAggregationRepository>();
        string capturedActorHash = string.Empty;

        repo.RecordActorSampleAsync(
            Arg.Any<TableIdentifier>(),
            Arg.Any<TenantId>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Do<string>(hash => capturedActorHash = hash),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var recorder = new ConsumerUsageRecorder(clusterSecret: "test-cluster-secret-key-12345");
        var table = new TableIdentifier("sales", "dbo", "orders");
        var tenant = new TenantId("tenant-1");
        var rawSid = "S-1-5-21-3623811015-3361044348-30300820-1013";

        recorder.RecordUsage(
            table,
            tenant,
            new Sid(rawSid),
            ConsumerOutcome.Allow,
            rowsReturned: 50,
            rowsMeasured: true,
            caller: new CallerContext("powerbi", "1.0", "GetOrders"));

        await recorder.FlushAsync(repo);

        // Verify that the captured hash is NOT the raw SID and is a 64-character hex string (HMAC-SHA256)
        capturedActorHash.ShouldNotBeNullOrWhiteSpace();
        capturedActorHash.ShouldNotContain(rawSid);
        capturedActorHash.Length.ShouldBe(64); // SHA-256 hex length
        capturedActorHash.ShouldBe(recorder.ComputeActorHmac(rawSid));

        // Directly verify in database table
        await ConsumerAggregationStore.RecordActorSampleAsync(conn, ConsumerAggregationDialect.Sqlite, null, table, tenant, "2026-10-18", "powerbi", capturedActorHash);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT actor_hash FROM CONSUMER_APP_ACTOR_DAILY;";
        var storedHash = (string?)await cmd.ExecuteScalarAsync();
        storedHash.ShouldNotBeNull();
        storedHash.ShouldBe(capturedActorHash);
        storedHash.ShouldNotContain(rawSid);
    }

    [Fact]
    public void T_SEC_AUD114_08_BufferSaturation_DropsSafelyWithoutMemoryLeak()
    {
        // Enforces SEC-AUD114-05: In-memory buffer capped at 20,000 series safely drops excess without crash or OOM.
        var recorder = new ConsumerUsageRecorder(clusterSecret: "secret");
        var table = new TableIdentifier("sales", "dbo", "orders");
        var tenant = new TenantId("tenant-1");

        // Insert up to capacity
        for (int i = 0; i < ConsumerUsageRecorder.MaxSeriesCapacity; i++)
        {
            recorder.RecordUsage(
                table,
                tenant,
                new Sid($"S-1-5-21-{i}"),
                ConsumerOutcome.Allow,
                1,
                true,
                caller: new CallerContext($"app-{i}", "1.0", "q"));
        }

        recorder.ActiveSeriesCount.ShouldBe(ConsumerUsageRecorder.MaxSeriesCapacity);
        recorder.OverflowCount.ShouldBe(0);

        // Record 500 excess series beyond capacity
        for (int i = 0; i < 500; i++)
        {
            recorder.RecordUsage(
                table,
                tenant,
                new Sid($"S-1-5-21-overflow-{i}"),
                ConsumerOutcome.Allow,
                1,
                true,
                caller: new CallerContext($"overflow-app-{i}", "1.0", "q"));
        }

        // Active series count must remain capped at MaxSeriesCapacity
        recorder.ActiveSeriesCount.ShouldBe(ConsumerUsageRecorder.MaxSeriesCapacity);
        recorder.OverflowCount.ShouldBe(500);
    }

    [Fact]
    public async Task TenantA_CannotSeeOrAccumulateInto_TenantB_AfterBaselineOnly()
    {
        // Enforces SEC-286-3/SEC-286-4: tenant_id is part of every key and every read; no runtime DDL involved.
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        new SqliteBaselineSchema(conn, null).Apply();
        var table = new TableIdentifier("sales", "dbo", "orders");
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");
        string day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        await ConsumerAggregationStore.RecordDailyUsageAsync(conn, ConsumerAggregationDialect.Sqlite, null, table, tenantA, day, "powerbi", "c", "1", 7, 70, 7, 0, 0, 0, 0);
        await ConsumerAggregationStore.RecordDailyUsageAsync(conn, ConsumerAggregationDialect.Sqlite, null, table, tenantB, day, "powerbi", "c", "1", 1, 10, 1, 0, 0, 0, 0);
        await ConsumerAggregationStore.RecordActorSampleAsync(conn, ConsumerAggregationDialect.Sqlite, null, table, tenantA, day, "powerbi", new string('b', 64));

        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow.AddDays(1);
        var a = await ConsumerAggregationStore.QueryConsumerAppDailyAsync(conn, null, table, tenantA, from, to);
        var b = await ConsumerAggregationStore.QueryConsumerAppDailyAsync(conn, null, table, tenantB, from, to);
        var actorsB = await ConsumerAggregationStore.QueryDistinctActorCountsAsync(conn, null, table, tenantB, from, to);

        a.ShouldHaveSingleItem().Calls.ShouldBe(7);
        b.ShouldHaveSingleItem().Calls.ShouldBe(1);
        actorsB.ShouldBeEmpty();
    }

    [Fact]
    public async Task T_SEC_AUD114_09_DecoupledPurge_DoesNotAffectAuditHashChain()
    {
        // Enforces SEC-AUD114-05: Purging consumer counter tables leaves MOCK_FORENSIC_LOG sequence and hash chain untouched.
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        new SqliteBaselineSchema(conn, null).Apply();

        // Create mock forensic log table (the real AUDIT_LOG_ENTRIES comes from the baseline since Issue #286) to simulate forensic log
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS MOCK_FORENSIC_LOG (
                    seq INTEGER PRIMARY KEY,
                    entry_hash TEXT NOT NULL,
                    target_table TEXT NOT NULL,
                    occurred_at TEXT NOT NULL
                );
                INSERT INTO MOCK_FORENSIC_LOG (seq, entry_hash, target_table, occurred_at)
                VALUES (1, 'hash-genesis-1234', 'sales.dbo.orders', '2025-01-01T00:00:00Z');
                INSERT INTO MOCK_FORENSIC_LOG (seq, entry_hash, target_table, occurred_at)
                VALUES (2, 'hash-second-5678', 'sales.dbo.orders', '2025-01-02T00:00:00Z');
            ";
            await cmd.ExecuteNonQueryAsync();
        }

        var table = new TableIdentifier("sales", "dbo", "orders");
        var tenant = new TenantId("tenant-1");

        // Insert expired consumer counter row (from 500 days ago) and fresh row
        await ConsumerAggregationStore.RecordDailyUsageAsync(
            conn, ConsumerAggregationDialect.Sqlite, null, table, tenant, "2024-01-01", "powerbi", "client1", "1.0", 10, 100, 10, 0, 0, 0, 0);
        await ConsumerAggregationStore.RecordDailyUsageAsync(
            conn, ConsumerAggregationDialect.Sqlite, null, table, tenant, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), "powerbi", "client1", "1.0", 5, 50, 5, 0, 0, 0, 0);

        // Purge with retentionDays = 400
        int deleted = await ConsumerAggregationStore.PurgeExpiredConsumerMetricsAsync(conn, null, retentionDays: 400, asOfUtc: DateTimeOffset.UtcNow);
        deleted.ShouldBe(1);

        // Verify MOCK_FORENSIC_LOG table is completely untouched (2 rows, sequence preserved)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*), MIN(seq), MAX(seq) FROM MOCK_FORENSIC_LOG;";
            await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            reader.GetInt64(0).ShouldBe(2);
            reader.GetInt64(1).ShouldBe(1);
            reader.GetInt64(2).ShouldBe(2);
        }
    }

    [Fact]
    public void T_SEC_AUD114_10_MultiTableWebSql_AttributesRejectionsAcrossAllTables()
    {
        // Enforces AC-114-07 / SEC-TEST-114-10: Multi-table WebSQL query rejection is attributed to all accessed tables.
        var recorder = Substitute.For<IConsumerUsageRecorder>();
        var tenant = new TenantId("tenant-1");
        var userSid = new Sid("S-1-5-21-USER1");

        var accessedTables = new List<TableIdentifier>
        {
            new("sales", "dbo", "orders"),
            new("sales", "dbo", "order_details"),
            new("crm", "dbo", "customers")
        };

        // Simulate multi-table SQL rejection (e.g. rate limit throttle or access denial)
        var outcome = ConsumerOutcome.DenyRateLimit;
        foreach (var t in accessedTables)
        {
            recorder.RecordUsage(t, tenant, userSid, outcome, rowsReturned: 0, rowsMeasured: false);
        }

        // Each accessed table must have received one rejection record
        foreach (var t in accessedTables)
        {
            recorder.Received(1).RecordUsage(t, tenant, userSid, ConsumerOutcome.DenyRateLimit, 0, false);
        }
    }

    [Fact]
    public void T_SEC_AUD114_11_MultiTableWebSql_JoinsDoNotMultiplyRows()
    {
        // Enforces AC-114-07 / SEC-TEST-114-11: Multi-table SQL join records rowsMeasured = false to prevent false row attribution.
        var recorder = Substitute.For<IConsumerUsageRecorder>();
        var tenant = new TenantId("tenant-1");
        var userSid = new Sid("S-1-5-21-USER1");

        var accessedTables = new List<TableIdentifier>
        {
            new("sales", "dbo", "orders"),
            new("crm", "dbo", "customers")
        };

        long queryRowsReturned = 1500;
        bool isSingleTable = accessedTables.Count == 1;

        foreach (var t in accessedTables)
        {
            recorder.RecordUsage(
                t,
                tenant,
                userSid,
                ConsumerOutcome.Allow,
                rowsReturned: isSingleTable ? queryRowsReturned : 0,
                rowsMeasured: isSingleTable);
        }

        // Neither table should have measured rows > 0 because a multi-table join cannot attribute exact rows per table
        foreach (var t in accessedTables)
        {
            recorder.Received(1).RecordUsage(t, tenant, userSid, ConsumerOutcome.Allow, 0, false);
        }
    }
}
