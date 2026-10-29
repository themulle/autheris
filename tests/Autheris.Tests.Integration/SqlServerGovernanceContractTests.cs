namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Contract tests that execute the real T-SQL of the SQL Server governance provider (schema, audit hash chain, application
/// lock, append-only protection, seeding). They need a SQL Server: either <c>AUTHERIS_TEST_MSSQL</c> (a connection string
/// to a server where the login may create databases) or Docker (Testcontainers). Without both, each test returns early.
/// </summary>
public sealed class SqlServerGovernanceContractTests : IAsyncLifetime
{
    private SqlServerTestDatabase? _db;
    private bool _available;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        _available = _db.IsAvailable;
        _connectionString = _db.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    private SqlServerGovernanceRepository NewRepository(bool seed = false)
    {
        var epoch = Substitute.For<IEpochValidationService>();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "SqlServer",
                ConnectionString = _connectionString!,
                EnableOutboxProcessor = false,
                SeedDemoData = seed
            }
        });
        return GovernanceStoreFactory.CreateSqlServer(epoch, options, env);
    }

    private static AuditLogEntry Entry(string type, string actor) => new()
    {
        TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant,
        EventType = type,
        ActorSid = new Sid(actor),
        TargetTable = "dbo.t",
        TraceId = Guid.NewGuid().ToString("N")
    };

    [Fact]
    public async Task TwoInstances_WritingConcurrently_KeepOneGaplessVerifiableChain()
    {
        if (!_available) return;

        await using var a = NewRepository();
        await using var b = NewRepository();

        var writers = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            var repo = i % 2 == 0 ? a : b;
            await repo.RecordAuditEventAsync(Entry("TEST_EVENT", "S-1-" + i));
        }));
        await Task.WhenAll(writers);
        await a.FlushAuditChannelAsync();
        await b.FlushAuditChannelAsync();

        (await a.VerifyAuditHashChainAsync()).ShouldBeTrue();
        (await b.VerifyAuditHashChainAsync()).ShouldBeTrue();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT_BIG(*), COUNT_BIG(DISTINCT seq), MAX(seq), MIN(seq) FROM AUDIT_LOG_ENTRIES", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        (await r.ReadAsync()).ShouldBeTrue();
        long count = r.GetInt64(0);
        r.GetInt64(1).ShouldBe(count);                       // no duplicate sequence numbers
        (r.GetInt64(2) - r.GetInt64(3) + 1).ShouldBe(count); // no gaps
        count.ShouldBeGreaterThanOrEqualTo(40);
    }

    [Fact]
    public async Task WP_PF_04_GroupCommitBatch_SpanningSeveralStatements_KeepsAGaplessVerifiableChain()
    {
        if (!_available) return;

        await using var repo = NewRepository();
        var batch = Enumerable.Range(0, 250).Select(i => Entry("TEST_EVENT", "S-1-" + i)).ToList();
        var concurrent = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            repo.RecordAuditEventsAsync(Enumerable.Range(0, 120).Select(i => Entry("TEST_EVENT", "S-2-" + i)).ToList())));

        await repo.RecordAuditEventsAsync(batch);
        await Task.WhenAll(concurrent);

        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
        batch[0].PrevHash.ShouldNotBeNullOrEmpty();
        batch[1].PrevHash.ShouldBe(batch[0].EntryHash);
        batch[249].PrevHash.ShouldBe(batch[248].EntryHash);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT_BIG(*), COUNT_BIG(DISTINCT seq), MAX(seq), MIN(seq) FROM AUDIT_LOG_ENTRIES", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        (await r.ReadAsync()).ShouldBeTrue();
        long count = r.GetInt64(0);
        r.GetInt64(1).ShouldBe(count);
        (r.GetInt64(2) - r.GetInt64(3) + 1).ShouldBe(count);
        count.ShouldBeGreaterThanOrEqualTo(250 + 3 * 120);
    }

    [Fact]
    public async Task WP_SC_10_AuditCursor_WalksTheChainOnceInSequenceOrder()
    {
        if (!_available) return;

        await using var repo = NewRepository();
        await repo.RecordAuditEventsAsync(Enumerable.Range(0, 35).Select(i => Entry("TEST_EVENT", "S-1-" + i)).ToList());

        var seen = new List<long>();
        long? before = null;
        do
        {
            var page = await repo.QueryAuditLogPageAsync(new Autheris.Application.Governance.Paging.AuditLogPageQuery(null, BeforeSequence: before, PageSize: 10));
            seen.AddRange(page.Items.Select(e => e.ChainSequence!.Value));
            before = page.NextBeforeSequence;
        }
        while (before != null);

        seen.Count.ShouldBeGreaterThanOrEqualTo(35);
        seen.Distinct().Count().ShouldBe(seen.Count);
        seen.ShouldBe(seen.OrderByDescending(s => s).ToList());
    }

    [Fact]
    public async Task AuditTable_RejectsUpdateDeleteAndTruncate()
    {
        if (!_available) return;

        await using var repo = NewRepository();
        await repo.RecordAuditEventAsync(Entry("TEST_EVENT", "S-1-1"));
        await repo.FlushAuditChannelAsync();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
                 {
                     "UPDATE AUDIT_LOG_ENTRIES SET decision = 'X'",
                     "DELETE FROM AUDIT_LOG_ENTRIES",
                     "TRUNCATE TABLE AUDIT_LOG_ENTRIES"
                 })
        {
            await using var cmd = new SqlCommand(sql, conn);
            await Should.ThrowAsync<SqlException>(() => cmd.ExecuteNonQueryAsync());
        }

        await using var count = new SqlCommand("SELECT COUNT_BIG(*) FROM AUDIT_LOG_ENTRIES", conn);
        ((long)(await count.ExecuteScalarAsync())!).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task SecondInstanceStart_WithExistingSchema_IsIdempotent()
    {
        if (!_available) return;

        await using var first = NewRepository();
        await first.RecordAuditEventAsync(Entry("TEST_EVENT", "S-1-1"));
        await first.FlushAuditChannelAsync();

        // A second replica starting against the same database must neither fail in DDL nor flag the chain.
        await using var second = NewRepository();
        (await second.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task DemoSeed_RunTwice_IsIdempotentAndCatalogIsReadable()
    {
        if (!_available) return;

        await using (var first = NewRepository(seed: true))
        {
            (await first.PingAsync()).ShouldBeTrue();
            var tables = await first.GetAllTablesAsync();
            tables.Count.ShouldBe(100);
        }

        await using var second = NewRepository(seed: true);
        (await second.GetAllTablesAsync()).Count.ShouldBe(100);
    }
}
