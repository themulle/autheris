using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>IConsumerAggregationRepository</c> (query-name counters and their retention purge) across PostgreSQL and SQL Server engines.
/// The counter key starts with the tenant; a repeated key accumulates instead of failing; the purge is global, bounded to 1..400 days and decoupled
/// from every other table. The usage reads that sit on the same counters are covered through <c>IUsageRepository</c>.
/// Owns the integration cases of SEC-286 (consumer aggregation tables in the governance baselines, bound upserts, visible flush failures).
/// </summary>
public abstract class ConsumerAggregationContract : GovernanceContractBase
{
    private const string Day = "2026-10-23";
    private static readonly TableIdentifier Table = new("sales", "dbo", "orders");
    private static readonly DateTimeOffset AsOf = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private IConsumerAggregationRepository Repo => Store.ConsumerAggregation;

    private static string DayOf(int offsetDays) => AsOf.AddDays(offsetDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<long> CallsAsync(TenantId tenant, string day = Day, string application = "powerbi", string query = "Q") =>
        Convert.ToInt64(await Store.ExecuteScalarAsync(
            "SELECT COALESCE(SUM(calls), 0) FROM CONSUMER_APP_QUERYNAME_DAILY WHERE tenant_id = @t AND table_id = @id AND day = @d AND application = @a AND query_name = @q",
            ("@t", tenant.Value), ("@id", Table.ToString()), ("@d", day), ("@a", application), ("@q", query)));

    private async Task<long> RowsAsync() =>
        Convert.ToInt64(await Store.ExecuteScalarAsync("SELECT COUNT(*) FROM CONSUMER_APP_QUERYNAME_DAILY"));

    private async Task<List<string>> DaysAsync()
    {
        var days = new List<string>();
        await using var conn = await Store.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT day FROM CONSUMER_APP_QUERYNAME_DAILY ORDER BY day";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            days.Add(reader.GetString(0));
        }

        return days;
    }

    private Task RecordAsync(TenantId tenant, string day, long calls, string query = "Q") =>
        Repo.RecordQueryNameAsync(Table, tenant, day, "powerbi", query, calls);

    private static void RecordUsage(UsageAggregator aggregator, TenantId tenant, Sid sid, CallerContext caller, int calls, UsageOutcome outcome = UsageOutcome.Allow)
    {
        var recorder = new UsageRecorder(aggregator);
        using var scope = UsageRequestScope.Begin(new UsageIdentity(tenant, sid, new HashSet<Sid>()), DataSourceTransportFlags.Rest, caller, aggregator);
        for (var i = 0; i < calls; i++)
        {
            recorder.Record(new UsageFact(Table, "sales", outcome, 10, true, 0, Array.Empty<GrantRef>(), default), tenant, sid);
        }

        scope.Complete(5, 64, 200);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await RecordAsync(Store.TenantA, Day, 3);
        await RecordAsync(Store.TenantB, Day, 5, "OtherQuery");

        (await CallsAsync(Store.TenantA)).ShouldBe(3);
        (await CallsAsync(Store.TenantA, query: "OtherQuery")).ShouldBe(0);
        (await CallsAsync(Store.TenantB)).ShouldBe(0);
        (await CallsAsync(Store.TenantB, query: "OtherQuery")).ShouldBe(5);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await RecordAsync(Store.TenantA, Day, 3);
        await RecordAsync(Store.TenantB, Day, 5);

        await RecordAsync(Store.TenantA, Day, 4);

        (await CallsAsync(Store.TenantA)).ShouldBe(7);
        (await CallsAsync(Store.TenantB)).ShouldBe(5);
        (await RowsAsync()).ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        Should.Throw<ArgumentException>(() => { _ = new TenantId("invalid tenant!"); });
        await Should.ThrowAsync<Exception>(() => RecordAsync(default, Day, 1));

        (await RowsAsync()).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        await RecordAsync(Store.TenantA, Day, 1);
        await RecordAsync(Store.TenantA, Day, 2);
        await RecordAsync(Store.TenantA, Day, 3);

        (await CallsAsync(Store.TenantA)).ShouldBe(6);
        (await RowsAsync()).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        // A repeated key accumulates and never raises a unique violation; every part of the key separates rows.
        await RecordAsync(Store.TenantA, Day, 1);
        await RecordAsync(Store.TenantA, Day, 1);
        await RecordAsync(Store.TenantA, DayOf(-1), 1);
        await RecordAsync(Store.TenantA, Day, 1, "Other");
        await Repo.RecordQueryNameAsync(Table, Store.TenantA, Day, "excel", "Q", 1);
        await Repo.RecordQueryNameAsync(new TableIdentifier("sales", "dbo", "invoices"), Store.TenantA, Day, "powerbi", "Q", 1);

        (await RowsAsync()).ShouldBe(5);
        (await CallsAsync(Store.TenantA)).ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        await RecordAsync(Store.TenantA, DayOf(-60), 1);
        await RecordAsync(Store.TenantA, DayOf(-40), 1);
        await RecordAsync(Store.TenantB, DayOf(-1), 1);
        await RecordAsync(Store.TenantA, DayOf(0), 1);

        // The purge works on the whole table in one pass and is stable: the second pass finds nothing, the survivors keep their day order.
        (await Repo.PurgeExpiredConsumerMetricsAsync(30, AsOf)).ShouldBe(2);
        (await Repo.PurgeExpiredConsumerMetricsAsync(30, AsOf)).ShouldBe(0);

        (await DaysAsync()).ShouldBe([DayOf(-1), DayOf(0)]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        await RecordAsync(Store.TenantA, DayOf(-401), 1);
        await RecordAsync(Store.TenantA, DayOf(-400), 1);
        await RecordAsync(Store.TenantA, DayOf(-2), 1);
        await RecordAsync(Store.TenantA, DayOf(-1), 1);
        await RecordAsync(Store.TenantA, DayOf(0), 1);

        // The retention window is clamped to 1..400 days: an absurd retention keeps at most 400 days, a non-positive one keeps at most one day.
        (await Repo.PurgeExpiredConsumerMetricsAsync(100000, AsOf)).ShouldBe(1);
        (await DaysAsync()).ShouldBe([DayOf(-400), DayOf(-2), DayOf(-1), DayOf(0)]);

        (await Repo.PurgeExpiredConsumerMetricsAsync(0, AsOf)).ShouldBe(2);
        (await Repo.PurgeExpiredConsumerMetricsAsync(-5, AsOf)).ShouldBe(0);
        (await DaysAsync()).ShouldBe([DayOf(-1), DayOf(0)]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        await RecordAsync(Store.TenantA, DayOf(-100), 4);
        await RecordAsync(Store.TenantA, DayOf(0), 2);

        (await Repo.PurgeExpiredConsumerMetricsAsync(30, AsOf)).ShouldBe(1);

        // A purged day is gone for reads (no tombstone) and a new count for it starts from zero.
        (await CallsAsync(Store.TenantA, DayOf(-100))).ShouldBe(0);
        (await CallsAsync(Store.TenantA, DayOf(0))).ShouldBe(2);
        await RecordAsync(Store.TenantA, DayOf(-100), 1);
        (await CallsAsync(Store.TenantA, DayOf(-100))).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ConcurrentActorInsert_SameKey_NoPkViolation()
    {
        // Concurrent upserts of the same key serialise: no primary key violation, no lost update, exact total.
        if (!Store.IsAvailable) return;
        var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => RecordAsync(Store.TenantA, day, 1)));

        (await RowsAsync()).ShouldBe(1);
        (await CallsAsync(Store.TenantA, day)).ShouldBe(24);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task T_SEC_AUD114_09_DecoupledPurge_DoesNotAffectAuditHashChain()
    {
        // Purging the query-name counter table leaves every other table untouched.
        if (!Store.IsAvailable) return;
        await Store.ExecuteNonQueryAsync(
            "CREATE TABLE MOCK_FORENSIC_LOG (seq BIGINT PRIMARY KEY, entry_hash VARCHAR(64) NOT NULL, target_table VARCHAR(128) NOT NULL, occurred_at VARCHAR(40) NOT NULL)");
        await Store.ExecuteNonQueryAsync(
            "INSERT INTO MOCK_FORENSIC_LOG (seq, entry_hash, target_table, occurred_at) VALUES (1, 'hash-genesis-1234', 'sales.dbo.orders', '2025-01-01T00:00:00Z')");
        await Store.ExecuteNonQueryAsync(
            "INSERT INTO MOCK_FORENSIC_LOG (seq, entry_hash, target_table, occurred_at) VALUES (2, 'hash-second-5678', 'sales.dbo.orders', '2025-01-02T00:00:00Z')");
        await RecordAsync(Store.TenantA, "2024-01-01", 10, "Q_old");
        await RecordAsync(Store.TenantA, DayOf(0), 5, "Q_new");

        (await Repo.PurgeExpiredConsumerMetricsAsync(400, AsOf)).ShouldBe(1);

        Convert.ToInt64(await Store.ExecuteScalarAsync("SELECT COUNT(*) FROM MOCK_FORENSIC_LOG")).ShouldBe(2);
        Convert.ToInt64(await Store.ExecuteScalarAsync("SELECT MIN(seq) FROM MOCK_FORENSIC_LOG")).ShouldBe(1);
        Convert.ToInt64(await Store.ExecuteScalarAsync("SELECT MAX(seq) FROM MOCK_FORENSIC_LOG")).ShouldBe(2);
        (await Store.ExecuteScalarAsync("SELECT entry_hash FROM MOCK_FORENSIC_LOG WHERE seq = 2")).ShouldBe("hash-second-5678");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task T_SEC_AUD114_07_ActorHash_IsStoredVerbatimAndNeverTheRawSid()
    {
        // With PseudonymizeUsers enabled, USAGE_DAILY keeps only the supplied HMAC value.
        if (!Store.IsAvailable) return;
        var hmacKey = Encoding.UTF8.GetBytes("test-cluster-secret-key-12345");
        var aggregator = new UsageAggregator(Options.Create(new UsageAccountingOptions { PseudonymizeUsers = true }), hmacKey);
        const string rawSid = "S-1-5-21-3623811015-3361044348-30300820-1013";
        var sid = new Sid(rawSid);
        var expectedKey = UsageAttribution.ComputeUserKey(sid, pseudonymizeUsers: true, hmacKey);

        RecordUsage(aggregator, Store.TenantA, sid, new CallerContext("powerbi"), 1);
        await Store.Usage.AccumulateAsync(aggregator.Swap());

        var stored = (string?)await Store.ExecuteScalarAsync(
            "SELECT user_key FROM USAGE_DAILY WHERE tenant_id = @t AND table_id = @id", ("@t", Store.TenantA.Value), ("@id", Table.ToString()));
        stored.ShouldBe(expectedKey);
        stored!.ShouldNotContain(rawSid);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task TenantA_CannotSeeOrAccumulateInto_TenantB_AfterBaselineOnly()
    {
        // The tenant is part of every key and every read; no runtime DDL is involved.
        if (!Store.IsAvailable) return;
        var aggregator = new UsageAggregator();
        var caller = new CallerContext("powerbi", ClientId: "c");
        RecordUsage(aggregator, Store.TenantA, new Sid("S-1-5-21-A"), caller, 7);
        RecordUsage(aggregator, Store.TenantB, new Sid("S-1-5-21-B"), caller, 1);

        await Store.Usage.AccumulateAsync(aggregator.Swap());

        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow.AddDays(1);
        var a = await Store.Usage.QueryConsumerAppDailyAsync(Table, Store.TenantA, from, to);
        var b = await Store.Usage.QueryConsumerAppDailyAsync(Table, Store.TenantB, from, to);

        a.ShouldHaveSingleItem().Calls.ShouldBe(7);
        b.ShouldHaveSingleItem().Calls.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task RecordAndQuery_RoundTripsAfterBaselineOnly()
    {
        if (!Store.IsAvailable) return;
        var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var aggregator = new UsageAggregator();
        var recorder = new UsageRecorder(aggregator);
        var caller = new CallerContext("powerbi", "1.0", ClientId: "c1");
        var sid = new Sid("S-1-5-21-a");

        using (var scope = UsageRequestScope.Begin(new UsageIdentity(Store.TenantA, sid, new HashSet<Sid>()), DataSourceTransportFlags.Rest, caller, aggregator))
        {
            recorder.Record(new UsageFact(Table, "sales", UsageOutcome.Allow, 20, true, 0, Array.Empty<GrantRef>(), default), Store.TenantA, sid);
            recorder.Record(new UsageFact(Table, "sales", UsageOutcome.Allow, 30, true, 0, Array.Empty<GrantRef>(), default), Store.TenantA, sid);
            recorder.Record(new UsageFact(Table, "sales", UsageOutcome.DenyAccess, 0, false, 0, Array.Empty<GrantRef>(), default), Store.TenantA, sid);
            recorder.Record(new UsageFact(Table, "sales", UsageOutcome.Allow, 0, false, 0, Array.Empty<GrantRef>(), default), Store.TenantA, sid);
            recorder.Record(new UsageFact(Table, "sales", UsageOutcome.Allow, 0, false, 0, Array.Empty<GrantRef>(), default), Store.TenantA, sid);
            scope.Complete(10, 100, 200);
        }

        await Store.Usage.AccumulateAsync(aggregator.Swap());
        await Repo.RecordQueryNameAsync(Table, Store.TenantA, day, "powerbi", "GetOrders", 5);

        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow.AddDays(1);
        var rows = await Store.Usage.QueryConsumerAppDailyAsync(Table, Store.TenantA, from, to);
        var actors = await Store.Usage.QueryDistinctActorCountsAsync(Table, Store.TenantA, from, to);

        var row = rows.ShouldHaveSingleItem();
        row.Calls.ShouldBe(5);
        row.RowsReturned.ShouldBe(50);
        row.Rejections.Access.ShouldBe(1);
        actors["powerbi"].ShouldBe(1);
        row.QueryNames.ShouldBe(["GetOrders"]);
    }

    [RequiresContainerRuntimeFact]
    public async Task Store_OnDatabaseWithoutTable_FailsAndNeverCreatesTables()
    {
        if (!Store.IsAvailable) return;
        await Store.ExecuteNonQueryAsync("DROP TABLE CONSUMER_APP_QUERYNAME_DAILY");

        // The runtime path never runs DDL: a missing table is an engine error, and the table is still missing afterwards.
        await Should.ThrowAsync<DbException>(() => RecordAsync(Store.TenantA, Day, 1));
        await Should.ThrowAsync<DbException>(() => Repo.PurgeExpiredConsumerMetricsAsync(30, AsOf));
        await Should.ThrowAsync<DbException>(() => RowsAsync());
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlConsumerAggregationContractTests(GovernancePostgresFixture fixture) : ConsumerAggregationContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "consumer_aggregation_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerConsumerAggregationContractTests(GovernanceSqlServerFixture fixture) : ConsumerAggregationContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "consumer_aggregation_contract");
}
