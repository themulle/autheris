namespace Autheris.Tests.Integration.Governance;

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SEC H-17: the public legacy HMAC key is gone; audit rows sealed with it never verify (PostgreSQL; its schema forbids NULL sequence numbers).</summary>
[Trait("Category", "Security")]
public sealed class LegacyAuditRowSecurityPostgreSqlTests
{
    private const string Genesis = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";

    private static (PostgreSqlGovernanceRepository Repo, string ConnectionString) Create(AuditChainViolationPolicy? policy = null)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { SeedDemoData = false },
            Audit = new AuditOptions { OnChainViolation = policy }
        });
        var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), out var cs, options, env);
        return (repo, cs);
    }

    private static void InsertLegacyRowSealedWithPublicConstant(string connectionString)
    {
        var id = Guid.NewGuid().ToString();
        var occurredAt = DateTimeOffset.UtcNow.ToString("O");
        var parsed = DateTimeOffset.Parse(occurredAt);
        var payload = $"{id}|{Genesis}|{parsed:O}|LEGACY|S-1-5-21-LEGACY|hr.dbo.salaries||ALLOW|trace-legacy|{{}}|legacy-single-tenant";
        var legacyHash = Convert.ToHexString(HMACSHA256.HashData("AutherisAuditLogHmacTamperEvidenceSecret2026!"u8.ToArray(), Encoding.UTF8.GetBytes(payload)));

        GovernanceTestDatabase.ExecuteRaw(
            connectionString,
            @"INSERT INTO AUDIT_LOG_ENTRIES (id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq)
              VALUES (@id, @occ, 'LEGACY', 'S-1-5-21-LEGACY', 'hr.dbo.salaries', NULL, 'ALLOW', 'trace-legacy', '{}', @prev, @hash, 'legacy-single-tenant', 1)",
            false,
            ("@id", id), ("@occ", occurredAt), ("@prev", Genesis), ("@hash", legacyHash));
    }

    [Fact]
    public async Task PostgreSql_Verify_RowSealedWithPublicConstant_Fails()
    {
        var (repo, cs) = Create();
        using (repo)
        {
            InsertLegacyRowSealedWithPublicConstant(cs);

            (await repo.VerifyAuditHashChainAsync()).ShouldBeFalse("A row sealed with the formerly public constant must no longer verify.");
            repo.IsAuditPipelineFaulted.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task PostgreSql_LegacySealedRow_LatchViolation_AndDenyDataAccessRejectsQueries()
    {
        var (repo, cs) = Create(AuditChainViolationPolicy.DenyDataAccess);
        using (repo)
        {
            InsertLegacyRowSealedWithPublicConstant(cs);
            (await repo.VerifyAuditHashChainAsync()).ShouldBeFalse();

            repo.IsAuditPipelineFaulted.ShouldBeTrue();
            await Should.ThrowAsync<AuditUnavailableException>(() => repo.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = Autheris.Tests.Common.TestTenants.Default,
                EventType = "TABLE_QUERY",
                ActorSid = new Sid("S-1-5-21-USER"),
                TargetTable = "hr.dbo.salaries",
                Decision = "ALLOW",
                TraceId = "trace-q"
            }));
        }
    }

    [Fact]
    public async Task PostgreSql_AuditRow_WithNullSequence_IsRejectedBySchema()
    {
        var (repo, cs) = Create();
        using (repo)
        {
            await Should.ThrowAsync<Npgsql.PostgresException>(() => Task.Run(() => GovernanceTestDatabase.ExecuteRaw(
                cs,
                @"INSERT INTO AUDIT_LOG_ENTRIES (id, occurred_at, event_type, actor_sid, target_table, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id)
                  VALUES ('x', 'now', 'LEGACY', 'S', 't', 'ALLOW', 'tr', '{}', 'p', 'h', 'legacy-single-tenant')")));
        }
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
