using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Infrastructure.Cache;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public sealed class AuditAnchorHardeningGovernanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-anchor-" + Guid.NewGuid().ToString("N"));


    public AuditAnchorHardeningGovernanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(_dir, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task Repository_WritesAnchorsToWormDirectory_AndDeletedLocalAnchorDoesNotHideTruncation()
    {
        var worm = Path.Combine(_dir, "worm");
        var anchor = Path.Combine(_dir, "local", "anchor.json");
        var options = RepoOptions(worm, anchor);
        string connectionString;
        using (var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), out connectionString, options))
        {
            for (var i = 1; i <= 4; i++)
            {
                await repo.RecordAuditEventAsync(NewEntry(i));
            }
            GovernanceTestDatabase.ExecuteRaw(connectionString, "DELETE FROM AUDIT_LOG_ENTRIES WHERE rowid IN (SELECT rowid FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC LIMIT 2)", bypassAppendOnlyTriggers: true);
        }

        Directory.GetFiles(worm).Length.ShouldBeGreaterThanOrEqualTo(4);
        File.Delete(anchor);

        using var restarted = GovernanceTestDatabase.CreateRepositoryOn(connectionString, new EpochValidationService(), options: options);
        (await restarted.VerifyAuditHashChainAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task SynchronousQueryAudit_CommitsBeforeReturning()
    {
        var options = RepoOptions(Path.Combine(_dir, "w2"), Path.Combine(_dir, "l2", "a.json"), syncQuery: true);
        using var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), options);
        await repo.RecordAuditEventAsync(NewEntry(1, "TABLE_QUERY"));

        Convert.ToInt32(GovernanceTestDatabase.ScalarRaw(GovernanceTestDatabase.ConnectionStringOf(repo), "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES")).ShouldBe(1); // no flush of the async channel needed
    }

    [Fact]
    public async Task Repository_TransactionRollback_DoesNotCorruptAuditChainOrTriggerViolation()
    {
        var worm = Path.Combine(_dir, "worm-rb");
        var anchor = Path.Combine(_dir, "local-rb", "anchor.json");
        var options = RepoOptions(worm, anchor, syncQuery: true);
        using var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), options);

        // 1. Initial valid audit entry
        await repo.RecordAuditEventAsync(NewEntry(1));
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
        var initialWormCount = Directory.Exists(worm) ? Directory.GetFiles(worm).Length : 0;

        // 2. Perform a transaction that rolls back after inserting an audit event
        await using (var connection = new Npgsql.NpgsqlConnection(GovernanceTestDatabase.ConnectionStringOf(repo)))
        {
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await repo.RecordAuditEventAsync(NewEntry(2), tx);
            await tx.RollbackAsync();
            repo.OnTransactionRolledBack(tx);
        }

        // 3. WORM directory must NOT contain an anchor for sequence 2 (count remains initialWormCount)
        var wormFilesAfterRollback = Directory.Exists(worm) ? Directory.GetFiles(worm).Length : 0;
        wormFilesAfterRollback.ShouldBe(initialWormCount);

        // 4. Verification must NOT fail or flag a violation
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();

        // 5. Subsequent audit event must succeed and chain properly
        await repo.RecordAuditEventAsync(NewEntry(3));
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();

        var wormFilesAfterCommit = Directory.Exists(worm) ? Directory.GetFiles(worm).Length : 0;
        wormFilesAfterCommit.ShouldBe(initialWormCount + 1);
    }

    private IOptions<GatewayOptions> RepoOptions(string worm, string anchor, bool syncQuery = false) => Options.Create(new GatewayOptions
    {
        GovernanceDb = new GovernanceDbOptions { SeedDemoData = false },
        Audit = new AuditOptions { ChainAnchorPath = anchor, ChainAnchorWormDirectory = worm, SynchronousQueryAudit = syncQuery }
    });

    private static AuditLogEntry NewEntry(int i, string? eventType = null) => new()
    {
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        EventType = eventType ?? $"EVENT_{i}",
        ActorSid = new Sid($"S-1-5-21-ACTOR-{i}"),
        TargetTable = "hr.dbo.salaries",
        Decision = "ALLOW",
        TraceId = $"trace-{i}",
        DetailsJson = "{}"
    };
}
