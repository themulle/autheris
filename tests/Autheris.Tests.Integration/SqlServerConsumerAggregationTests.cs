namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
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

    private async Task<(Autheris.Infrastructure.Persistence.SqlServerGovernanceRepository Repo, TableIdentifier Table)> NewRepositoryAsync()
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

        await repo.RecordDailyUsageAsync(table, tenant, day, "powerbi", "c1", "1.0", 2, 20, 2, 1, 0, 0, 0);
        await repo.RecordDailyUsageAsync(table, tenant, day, "powerbi", "c1", "1.0", 3, 30, 3, 0, 1, 0, 0);
        await repo.RecordActorSampleAsync(table, tenant, day, "powerbi", new string('a', 64));
        await repo.RecordActorSampleAsync(table, tenant, day, "powerbi", new string('a', 64));
        await repo.RecordActorSampleAsync(table, tenant, day, "powerbi", new string('b', 64));
        await repo.RecordQueryNameAsync(table, tenant, day, "powerbi", "GetReadings", 2);
        await repo.RecordQueryNameAsync(table, tenant, day, "powerbi", "GetReadings", 3);

        var rows = await repo.QueryConsumerAppDailyAsync(table, tenant, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var actors = await repo.QueryDistinctActorCountsAsync(table, tenant, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var row = rows.ShouldHaveSingleItem();
        row.Calls.ShouldBe(5);
        row.RowsReturned.ShouldBe(50);
        row.Rejections.Access.ShouldBe(1);
        row.Rejections.RateLimit.ShouldBe(1);
        row.DistinctActors.ShouldBe(2);
        row.QueryNames.ShouldBe(["GetReadings"]);
        actors["powerbi"].ShouldBe(2);

        var otherTenant = await repo.QueryConsumerAppDailyAsync(table, new TenantId("tenant-other"), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        otherTenant.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConcurrentActorInsert_SameKey_NoPkViolation()
    {
        // SEC-286-8: insert-if-absent with HOLDLOCK serialises the same key; no PK violation, exactly one row.
        if (!_shared.Db.IsAvailable)
        {
            return;
        }

        var (repo, table) = await NewRepositoryAsync();
        using var disposable = repo;
        var tenant = new TenantId("tenant-ss");
        string day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => repo.RecordActorSampleAsync(table, tenant, day, "powerbi", new string('c', 64))));
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => repo.RecordQueryNameAsync(table, tenant, day, "powerbi", "Q", 1)));

        var actors = await repo.QueryDistinctActorCountsAsync(table, tenant, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        actors["powerbi"].ShouldBe(1);

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
