using System.Collections.Generic;
using System.Text;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

/// <summary>
/// Issue #286 / WP-SIMPL-11: the usage & query-name counter stores on the migrated PostgreSQL governance schema.
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

    private static async Task<(PostgreSqlGovernanceRepository Repo, string ConnStr)> OpenRepoAsync()
    {
        var connStr = await GovernanceTestDatabase.CreateAsync("consumer_agg_" + Guid.NewGuid().ToString("N")[..12]);
        var repo = GovernanceSchemaTestSupport.UnmigratedPostgreSql(GovernanceSchemaTestSupport.PostgreSqlOptions(connStr, GovernanceMigrationMode.Verify));
        await repo.InitializeAsync();
        return (repo, connStr);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_AUD114_07_ActorHash_IsStoredVerbatimAndNeverTheRawSid()
    {
        // Enforces SEC-AUD114-04 on PostgreSQL: when PseudonymizeUsers is enabled, USAGE_DAILY keeps only the supplied HMAC value.
        var (repo, connStr) = await OpenRepoAsync();
        using var disposable = repo;
        byte[] hmacKey = Encoding.UTF8.GetBytes("test-cluster-secret-key-12345");
        var aggregator = new UsageAggregator(
            Options.Create(new UsageAccountingOptions { PseudonymizeUsers = true }),
            hmacKey);
        var recorder = new UsageRecorder(aggregator);
        var tenant = new TenantId("tenant-1");
        const string rawSid = "S-1-5-21-3623811015-3361044348-30300820-1013";
        var sid = new Sid(rawSid);
        string expectedKey = UsageAttribution.ComputeUserKey(sid, pseudonymizeUsers: true, hmacKey);

        using (var scope = UsageRequestScope.Begin(new UsageIdentity(tenant, sid, new HashSet<Sid>()), DataSourceTransportFlags.Rest, new CallerContext("powerbi"), aggregator))
        {
            recorder.Record(new UsageFact(Table, "sales", UsageOutcome.Allow, 10, true, 0, Array.Empty<GrantRef>(), default), tenant, sid);
            scope.Complete(5, 64, 200);
        }

        await repo.AccumulateAsync(aggregator.Swap());

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_key FROM USAGE_DAILY WHERE tenant_id = @t AND table_id = @id";
        cmd.Parameters.AddWithValue("@t", tenant.Value);
        cmd.Parameters.AddWithValue("@id", Table.ToString());
        var stored = (string?)await cmd.ExecuteScalarAsync();
        stored.ShouldBe(expectedKey);
        stored!.ShouldNotContain(rawSid);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TenantA_CannotSeeOrAccumulateInto_TenantB_AfterBaselineOnly()
    {
        // Enforces SEC-286-3/SEC-286-4: tenant_id is part of every key and every read; no runtime DDL involved.
        var (repo, _) = await OpenRepoAsync();
        using var disposable = repo;
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");
        var sidA = new Sid("S-1-5-21-A");
        var sidB = new Sid("S-1-5-21-B");
        var aggregator = new UsageAggregator();
        var recorder = new UsageRecorder(aggregator);
        var caller = new CallerContext("powerbi", ClientId: "c");

        using (var scope = UsageRequestScope.Begin(new UsageIdentity(tenantA, sidA, new HashSet<Sid>()), DataSourceTransportFlags.Rest, caller, aggregator))
        {
            for (int i = 0; i < 7; i++)
            {
                recorder.Record(new UsageFact(Table, "sales", UsageOutcome.Allow, 10, true, 0, Array.Empty<GrantRef>(), default), tenantA, sidA);
            }
            scope.Complete(5, 64, 200);
        }

        using (var scope = UsageRequestScope.Begin(new UsageIdentity(tenantB, sidB, new HashSet<Sid>()), DataSourceTransportFlags.Rest, caller, aggregator))
        {
            recorder.Record(new UsageFact(Table, "sales", UsageOutcome.Allow, 10, true, 0, Array.Empty<GrantRef>(), default), tenantB, sidB);
            scope.Complete(5, 64, 200);
        }

        await repo.AccumulateAsync(aggregator.Swap());

        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow.AddDays(1);
        var a = await repo.QueryConsumerAppDailyAsync(Table, tenantA, from, to);
        var b = await repo.QueryConsumerAppDailyAsync(Table, tenantB, from, to);

        a.ShouldHaveSingleItem().Calls.ShouldBe(7);
        b.ShouldHaveSingleItem().Calls.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_AUD114_09_DecoupledPurge_DoesNotAffectAuditHashChain()
    {
        // Enforces SEC-AUD114-05: purging query-name counter tables leaves the audit log rows untouched.
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

        await ConsumerAggregationStore.RecordQueryNameAsync(
            conn, ConsumerAggregationDialect.PostgreSql, null, Table, tenant, "2024-01-01", "powerbi", "Q_old", 10);
        await ConsumerAggregationStore.RecordQueryNameAsync(
            conn, ConsumerAggregationDialect.PostgreSql, null, Table, tenant, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), "powerbi", "Q_new", 5);

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
        var (repo, _) = await OpenRepoAsync();
        using var disposable = repo;
        var tenant = new TenantId("tenant-1");
        string day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var aggregator = new UsageAggregator();
        var recorder = new UsageRecorder(aggregator);
        var caller = new CallerContext("powerbi", "1.0", ClientId: "c1");
        var sid = new Sid("S-1-5-21-a");

        using (var scope = UsageRequestScope.Begin(new UsageIdentity(tenant, sid, new HashSet<Sid>()), DataSourceTransportFlags.Rest, caller, aggregator))
        {
            recorder.Record(new UsageFact(Table, "sales", UsageOutcome.Allow, 20, true, 0, Array.Empty<GrantRef>(), default), tenant, sid);
            recorder.Record(new UsageFact(Table, "sales", UsageOutcome.Allow, 30, true, 0, Array.Empty<GrantRef>(), default), tenant, sid);
            recorder.Record(new UsageFact(Table, "sales", UsageOutcome.DenyAccess, 0, false, 0, Array.Empty<GrantRef>(), default), tenant, sid);
            recorder.Record(new UsageFact(Table, "sales", UsageOutcome.Allow, 0, false, 0, Array.Empty<GrantRef>(), default), tenant, sid);
            recorder.Record(new UsageFact(Table, "sales", UsageOutcome.Allow, 0, false, 0, Array.Empty<GrantRef>(), default), tenant, sid);
            scope.Complete(10, 100, 200);
        }

        await repo.AccumulateAsync(aggregator.Swap());
        await repo.RecordQueryNameAsync(Table, tenant, day, "powerbi", "GetOrders", 5);

        var rows = await repo.QueryConsumerAppDailyAsync(Table, tenant, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var actors = await repo.QueryDistinctActorCountsAsync(Table, tenant, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var row = rows.ShouldHaveSingleItem();
        row.Calls.ShouldBe(5);
        row.RowsReturned.ShouldBe(50);
        row.Rejections.Access.ShouldBe(1);
        actors["powerbi"].ShouldBe(1);
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
