using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Observability;
using Autheris.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

/// <summary>
/// Issue #286 / WP-SIMPL-11: the CONSUMER_APP_* counter store on the migrated PostgreSQL governance schema. Replaces the SQLite
/// baseline variants of <c>ConsumerAggregationSecurityTests</c> and <c>ConsumerAggregationSqlTests</c>.
/// </summary>
public sealed class ConsumerAggregationPostgreSqlTests
{
    private static readonly TableIdentifier Table = new("sales", "dbo", "orders");

    private static async Task<NpgsqlConnection> OpenCloneAsync()
    {
        var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("consumer_agg_" + Guid.NewGuid().ToString("N")[..12]));
        await conn.OpenAsync();
        return conn;
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_AUD114_07_ActorHash_IsStoredVerbatimAndNeverTheRawSid()
    {
        // Enforces SEC-AUD114-04 on PostgreSQL: the counter table keeps only the supplied HMAC value.
        await using var conn = await OpenCloneAsync();
        var recorder = new ConsumerUsageRecorder(clusterSecret: "test-cluster-secret-key-12345");
        const string rawSid = "S-1-5-21-3623811015-3361044348-30300820-1013";
        var hash = recorder.ComputeActorHmac(rawSid);

        await ConsumerAggregationStore.RecordActorSampleAsync(conn, ConsumerAggregationDialect.PostgreSql, null, Table, new TenantId("tenant-1"), "2026-10-18", "powerbi", hash);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT actor_hash FROM CONSUMER_APP_ACTOR_DAILY";
        var stored = (string?)await cmd.ExecuteScalarAsync();
        stored.ShouldBe(hash);
        stored!.ShouldNotContain(rawSid);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TenantA_CannotSeeOrAccumulateInto_TenantB_AfterBaselineOnly()
    {
        // Enforces SEC-286-3/SEC-286-4: tenant_id is part of every key and every read; no runtime DDL involved.
        await using var conn = await OpenCloneAsync();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");
        string day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        await ConsumerAggregationStore.RecordDailyUsageAsync(conn, ConsumerAggregationDialect.PostgreSql, null, Table, tenantA, day, "powerbi", "c", "1", 7, 70, 7, 0, 0, 0, 0);
        await ConsumerAggregationStore.RecordDailyUsageAsync(conn, ConsumerAggregationDialect.PostgreSql, null, Table, tenantB, day, "powerbi", "c", "1", 1, 10, 1, 0, 0, 0, 0);
        await ConsumerAggregationStore.RecordActorSampleAsync(conn, ConsumerAggregationDialect.PostgreSql, null, Table, tenantA, day, "powerbi", new string('b', 64));

        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow.AddDays(1);
        var a = await ConsumerAggregationStore.QueryConsumerAppDailyAsync(conn, null, Table, tenantA, from, to);
        var b = await ConsumerAggregationStore.QueryConsumerAppDailyAsync(conn, null, Table, tenantB, from, to);
        var actorsB = await ConsumerAggregationStore.QueryDistinctActorCountsAsync(conn, null, Table, tenantB, from, to);

        a.ShouldHaveSingleItem().Calls.ShouldBe(7);
        b.ShouldHaveSingleItem().Calls.ShouldBe(1);
        actorsB.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_AUD114_09_DecoupledPurge_DoesNotAffectAuditHashChain()
    {
        // Enforces SEC-AUD114-05: purging consumer counter tables leaves the audit log rows untouched.
        await using var conn = await OpenCloneAsync();
        var tenant = new TenantId("tenant-1");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE MOCK_FORENSIC_LOG (seq BIGINT PRIMARY KEY, entry_hash TEXT NOT NULL, target_table TEXT NOT NULL, occurred_at TEXT NOT NULL);
                INSERT INTO MOCK_FORENSIC_LOG VALUES (1, 'hash-genesis-1234', 'sales.dbo.orders', '2025-01-01T00:00:00Z');
                INSERT INTO MOCK_FORENSIC_LOG VALUES (2, 'hash-second-5678', 'sales.dbo.orders', '2025-01-02T00:00:00Z');";
            await cmd.ExecuteNonQueryAsync();
        }

        await ConsumerAggregationStore.RecordDailyUsageAsync(
            conn, ConsumerAggregationDialect.PostgreSql, null, Table, tenant, "2024-01-01", "powerbi", "client1", "1.0", 10, 100, 10, 0, 0, 0, 0);
        await ConsumerAggregationStore.RecordDailyUsageAsync(
            conn, ConsumerAggregationDialect.PostgreSql, null, Table, tenant, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), "powerbi", "client1", "1.0", 5, 50, 5, 0, 0, 0, 0);

        int deleted = await ConsumerAggregationStore.PurgeExpiredConsumerMetricsAsync(conn, null, retentionDays: 400, asOfUtc: DateTimeOffset.UtcNow);
        deleted.ShouldBe(1);

        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*), MIN(seq), MAX(seq) FROM MOCK_FORENSIC_LOG";
        await using var reader = await check.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetInt64(0).ShouldBe(2);
        reader.GetInt64(1).ShouldBe(1);
        reader.GetInt64(2).ShouldBe(2);
    }

    [Fact]
    public async Task RecordAndQuery_PostgreSql_RoundTripsAfterBaselineOnly()
    {
        await using var conn = await OpenCloneAsync();
        var tenant = new TenantId("tenant-1");
        string day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        await ConsumerAggregationStore.RecordDailyUsageAsync(conn, ConsumerAggregationDialect.PostgreSql, null, Table, tenant, day, "powerbi", "c1", "1.0", 2, 20, 2, 0, 0, 0, 0);
        await ConsumerAggregationStore.RecordDailyUsageAsync(conn, ConsumerAggregationDialect.PostgreSql, null, Table, tenant, day, "powerbi", "c1", "1.0", 3, 30, 3, 1, 0, 0, 0);
        await ConsumerAggregationStore.RecordActorSampleAsync(conn, ConsumerAggregationDialect.PostgreSql, null, Table, tenant, day, "powerbi", new string('a', 64));
        await ConsumerAggregationStore.RecordActorSampleAsync(conn, ConsumerAggregationDialect.PostgreSql, null, Table, tenant, day, "powerbi", new string('a', 64));
        await ConsumerAggregationStore.RecordQueryNameAsync(conn, ConsumerAggregationDialect.PostgreSql, null, Table, tenant, day, "powerbi", "GetOrders", 5);

        var rows = await ConsumerAggregationStore.QueryConsumerAppDailyAsync(conn, null, Table, tenant, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var row = rows.ShouldHaveSingleItem();
        row.Calls.ShouldBe(5);
        row.RowsReturned.ShouldBe(50);
        row.Rejections.Access.ShouldBe(1);
        row.DistinctActors.ShouldBe(1);
        row.QueryNames.ShouldBe(["GetOrders"]);
    }

    [Fact]
    public async Task Store_OnDatabaseWithoutTable_FailsAndNeverCreatesTables()
    {
        await using var conn = await OpenCloneAsync();
        await using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP TABLE CONSUMER_APP_QUERYNAME_DAILY";
            await drop.ExecuteNonQueryAsync();
        }

        await Should.ThrowAsync<PostgresException>(() => ConsumerAggregationStore.RecordQueryNameAsync(
            conn, ConsumerAggregationDialect.PostgreSql, null, Table, new TenantId("t"), "2026-10-23", "app", "q", 1));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE lower(table_name) = 'consumer_app_queryname_daily'";
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).ShouldBe(0);
    }
}
