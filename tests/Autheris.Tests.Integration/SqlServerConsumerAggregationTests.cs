namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence.Migrations;
using Shouldly;
using Xunit;

/// <summary>Issue #286 (AC-4, SEC-286-3/8) on SQL Server: the MERGE upserts accumulate counters and never throw a primary key violation under concurrency.</summary>
public sealed class SqlServerConsumerAggregationTests : IClassFixture<SqlServerConsumerAggregationTests.MigratedDatabase>
{
    public sealed class MigratedDatabase : IAsyncLifetime
    {
        public SqlServerTestDatabase Db { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Db = await SqlServerTestDatabase.CreateAsync();
            if (Db.IsAvailable)
            {
                await GovernanceSchemaTestSupport.RunSqlServerAsync(Db.ConnectionString, GovernanceMigrationMode.Apply);
            }
        }

        public async Task DisposeAsync() => await Db.DisposeAsync();
    }

    private readonly MigratedDatabase _shared;

    public SqlServerConsumerAggregationTests(MigratedDatabase shared) => _shared = shared;

    private async Task<(SqlServerGovernanceRepository Repo, TableIdentifier Table)> NewRepositoryAsync()
    {
        var options = GovernanceSchemaTestSupport.SqlServerOptions(_shared.Db.ConnectionString, GovernanceMigrationMode.Verify);
        var repo = GovernanceSchemaTestSupport.UnmigratedSqlServer(options);
        await new SqlServerGovernanceMigrationRunner(repo, options).RunAsync();
        await repo.InitializeAsync();
        return (repo, new TableIdentifier("src", "dbo", "t_" + Guid.NewGuid().ToString("N")[..8]));
    }

    [Fact]
    public async Task RecordTwice_AccumulatesCounters_AndDistinctActorsCount()
    {
        if (!_shared.Db.IsAvailable)
        {
            return;
        }

        var (repo, table) = await NewRepositoryAsync();
        using var disposable = repo;
        var tenant = new TenantId("tenant-ss");
        string day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        var aggregator = new UsageAggregator();
        var recorder = new UsageRecorder(aggregator);
        var caller = new CallerContext("powerbi", "1.0", ClientId: "c1");
        var actor1 = new Sid("S-1-5-21-a");
        var actor2 = new Sid("S-1-5-21-b");

        using (var scope = UsageRequestScope.Begin(new UsageIdentity(tenant, actor1, new HashSet<Sid>()), DataSourceTransportFlags.Rest, caller, aggregator))
        {
            recorder.Record(new UsageFact(table, "src", UsageOutcome.Allow, 20, true, 0, Array.Empty<GrantRef>(), default), tenant, actor1);
            recorder.Record(new UsageFact(table, "src", UsageOutcome.DenyRateLimit, 0, false, 0, Array.Empty<GrantRef>(), default), tenant, actor1);
            recorder.Record(new UsageFact(table, "src", UsageOutcome.Allow, 30, true, 0, Array.Empty<GrantRef>(), default), tenant, actor1);
            scope.Complete(10, 100, 200);
        }

        using (var scope = UsageRequestScope.Begin(new UsageIdentity(tenant, actor2, new HashSet<Sid>()), DataSourceTransportFlags.Rest, caller, aggregator))
        {
            recorder.Record(new UsageFact(table, "src", UsageOutcome.DenyAccess, 0, false, 0, Array.Empty<GrantRef>(), default), tenant, actor2);
            recorder.Record(new UsageFact(table, "src", UsageOutcome.Allow, 0, false, 0, Array.Empty<GrantRef>(), default), tenant, actor2);
            scope.Complete(10, 100, 200);
        }

        await repo.AccumulateAsync(aggregator.Swap());
        await repo.RecordQueryNameAsync(table, tenant, day, "powerbi", "GetReadings", 2);
        await repo.RecordQueryNameAsync(table, tenant, day, "powerbi", "GetReadings", 3);

        var rows = await repo.QueryConsumerAppDailyAsync(table, tenant, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var actors = await repo.QueryDistinctActorCountsAsync(table, tenant, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var row = rows.ShouldHaveSingleItem();
        row.Calls.ShouldBe(5);
        row.RowsReturned.ShouldBe(50);
        row.Rejections.Access.ShouldBe(1);
        row.Rejections.RateLimit.ShouldBe(1);
        row.QueryNames.ShouldBe(["GetReadings"]);
        actors["powerbi"].ShouldBe(2);

        var otherTenant = await repo.QueryConsumerAppDailyAsync(table, new TenantId("tenant-other"), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        otherTenant.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConcurrentActorInsert_SameKey_NoPkViolation()
    {
        // SEC-286-8: concurrent MERGE upserts with HOLDLOCK serialise the same key; no PK violation, exact total.
        if (!_shared.Db.IsAvailable)
        {
            return;
        }

        var (repo, table) = await NewRepositoryAsync();
        using var disposable = repo;
        var tenant = new TenantId("tenant-ss");
        string day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => repo.RecordQueryNameAsync(table, tenant, day, "powerbi", "Q", 1)));

        // No lost update: one query-name row whose counter is the sum of all 24 concurrent increments.
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(_shared.Db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(calls), 0) FROM dbo.CONSUMER_APP_QUERYNAME_DAILY WHERE tenant_id = @t AND table_id = @id";
        cmd.Parameters.AddWithValue("@t", tenant.Value);
        cmd.Parameters.AddWithValue("@id", table.ToString());
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetInt32(0).ShouldBe(1);
        reader.GetInt64(1).ShouldBe(24);
    }
}
