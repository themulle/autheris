using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Audit;
using Autheris.Domain.Common;
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

/// <summary>SR-L-09: the public legacy HMAC key is gone; unsequenced SQLite rows need the explicit one-time migration.</summary>
[Trait("Category", "Security")]
public sealed class SqliteLegacyAuditMigrationTests : IDisposable
{
    private const string Genesis = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-legacy-audit-" + Guid.NewGuid().ToString("N"));

    public SqliteLegacyAuditMigrationTests() => Directory.CreateDirectory(_dir);

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

    private (IOptions<GatewayOptions> Options, string DbFile, string AnchorFile) FileOptions(AuditChainViolationPolicy? policy = null)
    {
        var dbFile = Path.Combine(_dir, "audit.db");
        var anchorFile = Path.Combine(_dir, "audit.anchor.json");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source={dbFile}", SeedDemoData = false },
            Audit = new AuditOptions { ChainAnchorPath = anchorFile, OnChainViolation = policy }
        });
        return (options, dbFile, anchorFile);
    }

    private static IHostEnvironment TestEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        return env;
    }

    private static void InsertLegacyRowSealedWithPublicConstant(string dbFile)
    {
        var id = Guid.NewGuid().ToString();
        var occurredAt = DateTimeOffset.UtcNow.ToString("O");
        var parsed = DateTimeOffset.Parse(occurredAt);
        var payload = $"{id}|{Genesis}|{parsed:O}|LEGACY|S-1-5-21-LEGACY|hr.dbo.salaries||ALLOW|trace-legacy|{{}}|legacy-single-tenant";
        var legacyHash = Convert.ToHexString(HMACSHA256.HashData("AutherisAuditLogHmacTamperEvidenceSecret2026!"u8.ToArray(), Encoding.UTF8.GetBytes(payload)));

        using var conn = new SqliteConnection($"Data Source={dbFile}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO AUDIT_LOG_ENTRIES (id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id)
                            VALUES (@id, @occ, 'LEGACY', 'S-1-5-21-LEGACY', 'hr.dbo.salaries', NULL, 'ALLOW', 'trace-legacy', '{}', @prev, @hash, 'legacy-single-tenant')";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@occ", occurredAt);
        cmd.Parameters.AddWithValue("@prev", Genesis);
        cmd.Parameters.AddWithValue("@hash", legacyHash);
        cmd.ExecuteNonQuery();
    }

    private static AuditLogEntry NewEntry(int i) => new()
    {
        EventType = $"EVENT_{i}",
        ActorSid = new Sid($"S-1-5-21-ACTOR-{i}"),
        TargetTable = "hr.dbo.salaries",
        Decision = "ALLOW",
        TraceId = $"trace-{i}",
        DetailsJson = "{}"
    };

    private void CreateSchemaThenInsertLegacyRow(IOptions<GatewayOptions> options, string dbFile, string anchorFile)
    {
        using (GovernanceStoreFactory.CreateSqlite(new EpochValidationService(), options, TestEnvironment()))
        {
            // creates the schema
        }

        InsertLegacyRowSealedWithPublicConstant(dbFile);
        File.Delete(anchorFile);
    }

    [Fact]
    public async Task Sqlite_Verify_NullSeqRowSealedWithPublicConstant_Fails()
    {
        var (options, dbFile, anchorFile) = FileOptions();
        CreateSchemaThenInsertLegacyRow(options, dbFile, anchorFile);

        using var repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService(), options, TestEnvironment());

        (await repo.VerifyAuditHashChainAsync()).ShouldBeFalse("A row sealed with the formerly public constant must no longer verify.");
        repo.IsAuditPipelineFaulted.ShouldBeTrue();
    }

    [Fact]
    public async Task Sqlite_LegacyRows_WithoutFlag_LatchViolation_AndDenyDataAccessRejectsQueries()
    {
        var (options, dbFile, anchorFile) = FileOptions(AuditChainViolationPolicy.DenyDataAccess);
        CreateSchemaThenInsertLegacyRow(options, dbFile, anchorFile);

        using var repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService(), options, TestEnvironment());

        repo.IsAuditPipelineFaulted.ShouldBeTrue("Startup must latch a violation for unsequenced rows when the flag is off.");
        await Should.ThrowAsync<AuditUnavailableException>(() => repo.RecordAuditEventAsync(new AuditLogEntry
        {
            EventType = "TABLE_QUERY",
            ActorSid = new Sid("S-1-5-21-USER"),
            TargetTable = "hr.dbo.salaries",
            Decision = "ALLOW",
            TraceId = "trace-q"
        }));
    }

    [Fact]
    public async Task Sqlite_LegacyMigration_ResealsAndRecordsBoundary()
    {
        var (options, dbFile, anchorFile) = FileOptions();
        CreateSchemaThenInsertLegacyRow(options, dbFile, anchorFile);
        var migration = Options.Create(new AuditLegacyMigrationOptions { AllowLegacyUnsequencedRows = true });

        using var repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService(), options, TestEnvironment(), legacyMigrationOptions: migration);

        repo.IsAuditPipelineFaulted.ShouldBeFalse();
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue("Re-sealed rows must verify under the real audit key.");

        using (var cmd = repo.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT seq FROM AUDIT_LOG_ENTRIES";
            Convert.ToInt64(await cmd.ExecuteScalarAsync()).ShouldBe(1);
        }

        using (var cmd = repo.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES_legacy_backup WHERE seq IS NULL";
            Convert.ToInt64(await cmd.ExecuteScalarAsync()).ShouldBe(1);
        }

        repo.GetVerifiedChainAnchor()!.Sequence.ShouldBe(1);

        await repo.RecordAuditEventAsync(NewEntry(2));
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
        using var seqCmd = repo.Connection.CreateCommand();
        seqCmd.CommandText = "SELECT seq FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC LIMIT 1";
        Convert.ToInt64(await seqCmd.ExecuteScalarAsync()).ShouldBe(2);
    }

    [Fact]
    public void LegacyHmacConstant_NoLongerExistsInProductionSources()
    {
        var root = FindRepositoryRoot();
        var hits = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("AutherisAuditLogHmacTamperEvidenceSecret2026!", StringComparison.Ordinal))
            .ToList();

        hits.ShouldBeEmpty();
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Autheris.sln) not found.");
    }
}
