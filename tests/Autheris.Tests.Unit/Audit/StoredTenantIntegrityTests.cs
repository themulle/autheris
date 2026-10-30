using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>DT-A-9 / DT-SC-04 / DT-SC-16 (WP-DT-2a): no tenant fallback in stored values, writers or audit hashing.</summary>
[Trait("Category", "Security")]
public sealed class StoredTenantIntegrityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-stored-tenant-" + Guid.NewGuid().ToString("N"));

    public StoredTenantIntegrityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string Db => Path.Combine(_dir, "gov.db");

    private SqliteGovernanceRepository CreateRepository()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source={Db}", SeedDemoData = false },
            Audit = new AuditOptions { ChainAnchorPath = Path.Combine(_dir, "anchor.json") }
        });
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        return GovernanceStoreFactory.CreateSqlite(new EpochValidationService(), options, env, auditAnchorStore: new FileAuditChainAnchorStore(Path.Combine(_dir, "anchor.json")));
    }

    private static AuditLogEntry Entry(TenantId tenant) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenant,
        EventType = AuditEventTypes.AuthSucceeded,
        ActorSid = new Sid("S-1-5-21-1"),
        TargetTable = "dbo.Users",
        Decision = "ALLOW",
        TraceId = "t",
        DetailsJson = "{}",
        OccurredAt = DateTimeOffset.UtcNow
    };

    private void Exec(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={Db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad value")]
    [InlineData("_platform")]
    public void DT_SC_16_Parse_Invalid_Throws_Without_Raw_Value(string? raw)
    {
        var ex = Should.Throw<StoredTenantIntegrityException>(() => StoredTenant.Parse(raw));
        ex.ErrorCode.ShouldBe("STORED_TENANT_INVALID");
        ex.InnerException.ShouldBeNull();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            ex.ToString().ShouldNotContain(raw);
        }
    }

    [Fact]
    public void DT_SC_16_ParseAudit_Accepts_Platform_And_Valid_Rejects_Rest()
    {
        StoredTenant.ParseAudit("_platform").ShouldBe(TenantId.Platform);
        StoredTenant.ParseAudit("tenant-a").Value.ShouldBe("tenant-a");
        Should.Throw<StoredTenantIntegrityException>(() => StoredTenant.ParseAudit(null));
        Should.Throw<StoredTenantIntegrityException>(() => StoredTenant.ParseAudit("_other"));
    }

    [Fact]
    public void DT_SC_04_RequireWritable_Default_Throws()
    {
        Should.Throw<ArgumentException>(() => StoredTenant.RequireWritable(default));
        StoredTenant.RequireWritable(new TenantId("t1")).ShouldBe("t1");
        StoredTenant.RequireWritable(TenantId.Platform).ShouldBe("_platform");
    }

    [Fact]
    public void DT_SC_04_AuditChainEngine_Default_Tenant_Throws_And_Valid_Hash_Unchanged()
    {
        var key = new byte[32];
        var bad = new[] { Entry(default) };
        Should.Throw<ArgumentException>(() => AuditChainEngine.LinkBatch(key, bad, 0, AuditChainEngine.AuditGenesisHash));
        Should.Throw<ArgumentException>(() => AuditChainEngine.ComputeBatchChain(bad, null, key));

        var good = Entry(new TenantId("tenant-a"));
        AuditChainEngine.LinkBatch(key, new[] { good }, 0, AuditChainEngine.AuditGenesisHash);
        var expected = AuditChainEngine.ComputeEntryHash(key, 1, good.Id.ToString(), good.PrevHash, good.OccurredAt, good.EventType,
            good.ActorSid.Value, good.TargetTable, good.TargetColumn, good.Decision, good.TraceId, good.DetailsJson, "tenant-a");
        good.EntryHash.ShouldBe(expected);
    }

    [Fact]
    public void DT_SC_04_Entry_Hash_Matches_The_V1_5_0_Golden()
    {
        // Literal golden: HMAC-SHA256 (32 zero bytes) over the v1.5.0 canonical payload
        // "v2|1|<id>|GENESIS_<64 zeros>|2026-01-01T00:00:00.0000000+00:00|auth.succeeded|S-1-5-21-1|dbo.Users||ALLOW|trace-1|{}|tenant-a",
        // computed independently of the current code. A change of the canonicalisation breaks every stored audit chain.
        var hash = AuditChainEngine.ComputeEntryHash(
            new byte[32], 1, "00000000-0000-0000-0000-000000000001", AuditChainEngine.AuditGenesisHash,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "auth.succeeded", "S-1-5-21-1", "dbo.Users", null, "ALLOW", "trace-1", "{}", "tenant-a");
        hash.ShouldBe("607FFE72122C4B28E1A646F736DAA35F8224A7B2C5C985791182D55D00A16482");
    }

    [Fact]
    public async Task DT_SC_04_Sqlite_Audit_Write_Without_Tenant_Throws()
    {
        using var repo = CreateRepository();
        await Should.ThrowAsync<ArgumentException>(() => repo.RecordAuditEventAsync(Entry(default)));
    }

    [Fact]
    public async Task DT_SC_04_Sqlite_Consent_Write_Without_Tenant_Throws()
    {
        using var repo = CreateRepository();
        var consent = new Consent { TableId = Guid.NewGuid(), GranteeSid = new Sid("S-1-5-21-1"), TenantId = default };
        await Should.ThrowAsync<ArgumentException>(() => repo.CreateConsentAsync(consent));
    }

    [Fact]
    public async Task DT_SC_16_Sqlite_Audit_Reader_Invalid_Stored_Tenant_Fails_Closed()
    {
        using var repo = CreateRepository();
        await repo.RecordAuditEventAsync(Entry(new TenantId("tenant-a")));
        await repo.FlushAuditChannelAsync();
        Exec("UPDATE AUDIT_LOG_ENTRIES SET tenant_id = 'bad value'");
        var ex = await Should.ThrowAsync<StoredTenantIntegrityException>(() => repo.GetAuditLogEntriesAsync(10));
        ex.ToString().ShouldNotContain("bad value");
    }

    [Fact]
    public async Task DT_SC_16_Sqlite_Chain_Verification_Fails_For_Unparsable_Stored_Tenant()
    {
        using var repo = CreateRepository();
        await repo.RecordAuditEventAsync(Entry(new TenantId("tenant-a")));
        await repo.FlushAuditChannelAsync();
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
        Exec("UPDATE AUDIT_LOG_ENTRIES SET tenant_id = 'bad value'");
        (await repo.VerifyAuditHashChainAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task AuthFailure_Without_Tenant_Goes_To_Platform()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        var recorded = new System.Collections.Generic.List<AuditLogEntry>();
        repo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(recorded.Add), Arg.Any<System.Threading.CancellationToken>()).Returns(Task.CompletedTask);
        var aggregator = new AuthFailureAggregator(repo, Options.Create(new GatewayOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthFailureAggregator>.Instance);

        await aggregator.RecordFailureAsync("10.0.0.1", "INVALID", "u", null);
        await aggregator.RecordFailureAsync("10.0.0.2", "INVALID", "u", "bad value");

        recorded.Select(e => e.TenantId).ShouldAllBe(t => t == TenantId.Platform);
    }

    [Fact]
    public void AuditContext_Has_No_Tenant_Default()
    {
        new AuditContext().TenantId.ShouldBe(default);
    }
}
