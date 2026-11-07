using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public sealed class AuditArchitectureHardeningGovernanceTests : IDisposable
{
    private readonly string _testDir;


    public AuditArchitectureHardeningGovernanceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "autheris-au-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Array.Empty<string>())
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(_testDir, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task AU04_PostgreSqlRepository_EnrolledTransaction_Commit_PersistsEntryAndChainsCorrectly()
    {
        var wormPath = Path.Combine(_testDir, "worm_commit");
        var anchorPath = Path.Combine(_testDir, "anchor_commit.json");

        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions(),
            Audit = new AuditOptions
            {
                ChainAnchorPath = anchorPath,
                ChainAnchorWormDirectory = wormPath,
                SynchronousQueryAudit = true
            }
        });

        using var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), options);

        var entry1 = CreateAuditEntry(1, "CONSENT_CREATED");
        await using (var connection = new Npgsql.NpgsqlConnection(GovernanceTestDatabase.ConnectionStringOf(repo)))
        {
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await repo.RecordAuditEventAsync(entry1, tx);
            await tx.CommitAsync();
            repo.OnTransactionCommitted(tx);
        }

        var isValid = await repo.VerifyAuditHashChainAsync();
        isValid.ShouldBeTrue();

        var count = Convert.ToInt32(GovernanceTestDatabase.ScalarRaw(GovernanceTestDatabase.ConnectionStringOf(repo), "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE EVENT_TYPE = 'CONSENT_CREATED'"));
        count.ShouldBe(1);
    }

    [Fact]
    public async Task AU04_PostgreSqlRepository_EnrolledTransaction_Rollback_RestoresPointerAndPreservesChain()
    {
        var wormPath = Path.Combine(_testDir, "worm_rollback");
        var anchorPath = Path.Combine(_testDir, "anchor_rollback.json");

        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions(),
            Audit = new AuditOptions
            {
                ChainAnchorPath = anchorPath,
                ChainAnchorWormDirectory = wormPath,
                SynchronousQueryAudit = true
            }
        });

        using var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), options);

        // 1. Initial committed entry
        var entry1 = CreateAuditEntry(1, "CONSENT_INITIAL");
        await repo.RecordAuditEventAsync(entry1);
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();

        // 2. Transaction that is rolled back
        var entry2 = CreateAuditEntry(2, "CONSENT_FAILED_ATTEMPT");
        await using (var connection = new Npgsql.NpgsqlConnection(GovernanceTestDatabase.ConnectionStringOf(repo)))
        {
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await repo.RecordAuditEventAsync(entry2, tx);
            await tx.RollbackAsync();
            repo.OnTransactionRolledBack(tx);
        }

        // 3. Rollback must not corrupt audit hash chain
        var chainValidAfterRollback = await repo.VerifyAuditHashChainAsync();
        chainValidAfterRollback.ShouldBeTrue();

        // 4. Failed entry must not exist in table
        var count = Convert.ToInt32(GovernanceTestDatabase.ScalarRaw(GovernanceTestDatabase.ConnectionStringOf(repo), "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE EVENT_TYPE = 'CONSENT_FAILED_ATTEMPT'"));
        count.ShouldBe(0);

        // 5. Subsequent audit entry must succeed and chain properly from entry1
        var entry3 = CreateAuditEntry(3, "CONSENT_RETRY_SUCCESS");
        await repo.RecordAuditEventAsync(entry3);
        var chainValidAfterSubsequent = await repo.VerifyAuditHashChainAsync();
        chainValidAfterSubsequent.ShouldBeTrue();
    }

    private static AuditLogEntry CreateAuditEntry(int sequence, string eventType) => new()
    {
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        EventType = eventType,
        ActorSid = new Sid($"S-1-5-21-ACTOR-{sequence}"),
        TargetTable = "finance.accounts",
        Decision = "ALLOW",
        TraceId = $"trace-{sequence}-{Guid.NewGuid():N}",
        DetailsJson = "{}"
    };
}
