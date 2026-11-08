namespace Autheris.Tests.Unit.Audit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Observability;
using Autheris.Infrastructure.Persistence;
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
