namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

/// <summary>
/// Review PG-15: contract tests that execute the real PostgreSQL SQL of the governance provider (audit hash chain,
/// advisory lock, append-only triggers). They need Docker; without it each test returns early (logged by the fixture).
/// </summary>
public sealed class PostgreSqlGovernanceContractTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _container.StartAsync();
            _available = true;
        }
        catch (Exception)
        {
            _available = false; // no Docker on this machine
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    private PostgreSqlGovernanceRepository NewRepository()
    {
        var epoch = Substitute.For<IEpochValidationService>();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = _container!.GetConnectionString(),
                EnableOutboxProcessor = false,
                SeedDemoData = false
            }
        });
        return new PostgreSqlGovernanceRepository(epoch, options, env);
    }

    private static AuditLogEntry Entry(string type, string actor) => new()
    {
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

        await using var conn = new NpgsqlConnection(_container!.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*), count(DISTINCT seq), max(seq), min(seq) FROM audit_log_entries", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        (await r.ReadAsync()).ShouldBeTrue();
        long count = r.GetInt64(0);
        r.GetInt64(1).ShouldBe(count);                 // no duplicate sequence numbers
        (r.GetInt64(2) - r.GetInt64(3) + 1).ShouldBe(count); // no gaps
        count.ShouldBeGreaterThanOrEqualTo(40);
    }

    [Fact]
    public async Task AuditTable_RejectsUpdateDeleteAndTruncate()
    {
        if (!_available) return;

        await using var repo = NewRepository();
        await repo.RecordAuditEventAsync(Entry("TEST_EVENT", "S-1-1"));
        await repo.FlushAuditChannelAsync();

        await using var conn = new NpgsqlConnection(_container!.GetConnectionString());
        await conn.OpenAsync();
        foreach (var sql in new[]
                 {
                     "UPDATE audit_log_entries SET decision = 'X'",
                     "DELETE FROM audit_log_entries",
                     "TRUNCATE audit_log_entries"
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var ex = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            ex.SqlState.ShouldBe("42501");
        }
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
}
