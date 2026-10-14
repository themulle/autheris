using System.Globalization;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// WP-ARC-B-13: the SQLite governance repository runs on pooled per-operation connections. One write gate serializes every write
/// (the audit chain cannot fork, four-eyes approvals stay atomic) while reads run concurrently with the writer.
/// </summary>
[Trait("Category", "Security")]
public sealed class SqliteGovernanceConcurrencyTests : IDisposable
{
    private static readonly TableIdentifier FourEyesTable = new("finance", "dbo", "finance_table_5");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "autheris-sqlite-concurrency-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteGovernanceRepository _repository;

    public SqliteGovernanceConcurrencyTests()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = $"Data Source={Path.Combine(_directory, "governance.db")}",
                JournalMode = "WAL",
                SeedDemoData = true
            },
            Audit = new AuditOptions { ChainAnchorPath = Path.Combine(_directory, "anchor.json"), SynchronousQueryAudit = true }
        });
        _repository = GovernanceStoreFactory.CreateSqlite(new EpochValidationService(), options);
    }

    public void Dispose()
    {
        _repository.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static AuditLogEntry NewAudit(int i) => new()
    {
        EventType = "CONCURRENT_ADMIN_ACTION",
        ActorSid = new Sid($"S-1-5-21-WRITER-{i % 7}"),
        TargetTable = "finance.dbo.finance_table_1",
        TraceId = $"trace-{i:D5}",
        DetailsJson = $"{{\"i\":{i}}}"
    };

    private async Task<ConsentRequest> NewFourEyesRequestAsync(string requester)
    {
        var meta = await _repository.GetTableMetadataAsync(FourEyesTable);
        meta.ShouldNotBeNull();
        return await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = FourEyesTable,
            RequesterSid = new Sid(requester),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester,
            BusinessJustification = "Concurrency test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });
    }

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = _repository.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task ConcurrentCatalogReads_DoNotBlockOnAuditWrite()
    {
        // Hold the write gate exactly like an audit batch or a consent write would.
        await _repository.LockUnguardedAsync();
        try
        {
            var reads = Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                {
                    (await _repository.GetAllTablesAsync()).ShouldNotBeEmpty();
                    (await _repository.GetConsentRequestAsync(Guid.NewGuid())).ShouldBeNull();
                    (await _repository.GetTransitiveRolesAsync(new Sid("S-1-5-21-NOBODY"))).ShouldNotBeNull();
                    (await _repository.GetTableEpochAsync(FourEyesTable)).ShouldBeGreaterThanOrEqualTo(0);
                })));

            await reads.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            _repository.ReleaseLock();
        }
    }

    [Fact]
    public async Task Writes_WaitForTheWriteGate()
    {
        await _repository.LockUnguardedAsync();
        Task write;
        try
        {
            write = _repository.RecordAuditEventAsync(NewAudit(1));
            await Task.Delay(300);
            write.IsCompleted.ShouldBeFalse("a write must wait while another writer holds the gate");
        }
        finally
        {
            _repository.ReleaseLock();
        }

        await write.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AuditChain_ConcurrentWriters_RemainSequentialAndVerifiable()
    {
        const int writers = 8;
        const int perWriter = 50;

        var auditWriters = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                await _repository.RecordAuditEventAsync(NewAudit((w * perWriter) + i));
            }
        }));

        // Business transactions audit inside their own transaction and advance the same in-memory chain tail.
        var approvals = Enumerable.Range(0, 6).Select(n => Task.Run(async () =>
        {
            var request = await NewFourEyesRequestAsync($"S-1-5-21-REQ-CHAIN-{n}");
            await _repository.ApproveConsentRequestStepAsync(request.Id, new Sid("S-1-5-21-APPROVER-A"));
        }));

        await Task.WhenAll(auditWriters.Concat(approvals));

        (await _repository.VerifyAuditHashChainAsync()).ShouldBeTrue();
        _repository.IsAuditPipelineFaulted.ShouldBeFalse();

        var total = await ScalarAsync("SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES");
        total.ShouldBeGreaterThanOrEqualTo(writers * perWriter + 6);
        (await ScalarAsync("SELECT MAX(seq) FROM AUDIT_LOG_ENTRIES")).ShouldBe(total, "the sequence is contiguous: max equals the row count");
        (await ScalarAsync("SELECT COUNT(DISTINCT seq) FROM AUDIT_LOG_ENTRIES")).ShouldBe(total);
        (await ScalarAsync("SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE seq IS NULL")).ShouldBe(0, "nothing is latched as unsequenced");
    }

    [Fact]
    public async Task ConcurrentApprovalsBySameApprover_RecordExactlyOneStep()
    {
        var request = await NewFourEyesRequestAsync("S-1-5-21-REQ-SAME-APPROVER");
        var approver = new Sid("S-1-5-21-APPROVER-A");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                await _repository.ApproveConsentRequestStepAsync(request.Id, approver);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        })));

        attempts.Count(succeeded => succeeded).ShouldBe(1, "the four-eyes check and its write are atomic");
        (await ScalarAsync("SELECT COUNT(*) FROM APPROVAL_STEPS WHERE consent_request_id = @id", ("@id", request.Id.ToString()))).ShouldBe(1);
        (await _repository.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        (await _repository.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task ConcurrentApprovalsByTwoApprovers_NeverCompleteWithoutTwoDistinctSteps()
    {
        var request = await NewFourEyesRequestAsync("S-1-5-21-REQ-TWO-APPROVERS");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(n => Task.Run(async () =>
            {
                try
                {
                    await _repository.ApproveConsentRequestStepAsync(request.Id, new Sid(n % 2 == 0 ? "S-1-5-21-APPROVER-A" : "S-1-5-21-APPROVER-B"));
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })));

        results.Count(r => r).ShouldBe(2, "exactly one step per distinct approver");
        (await ScalarAsync("SELECT COUNT(DISTINCT approver_sid) FROM APPROVAL_STEPS WHERE consent_request_id = @id", ("@id", request.Id.ToString()))).ShouldBe(2);
    }

    [Fact]
    public async Task ConcurrentApproveAndReject_EndInOneConsistentState()
    {
        for (var round = 0; round < 5; round++)
        {
            var request = await NewFourEyesRequestAsync($"S-1-5-21-REQ-RACE-{round}");

            var approve = Task.Run(async () =>
            {
                try
                {
                    await _repository.ApproveConsentRequestStepAsync(request.Id, new Sid("S-1-5-21-APPROVER-A"));
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });
            var reject = Task.Run(async () =>
            {
                try
                {
                    await _repository.RejectConsentRequestAsync(request.Id, new Sid("S-1-5-21-APPROVER-B"), "race");
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });
            var approved = await approve;
            var rejected = await reject;

            var status = (await _repository.GetConsentRequestAsync(request.Id))!.Status;
            var steps = await ScalarAsync("SELECT COUNT(*) FROM APPROVAL_STEPS WHERE consent_request_id = @id AND decision = 'APPROVED'", ("@id", request.Id.ToString()));
            var stepAudits = await ScalarAsync(
                "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE event_type = 'CONSENT_APPROVAL_STEP' AND details_json LIKE @needle",
                ("@needle", $"%{request.Id}%"));

            // A rejection always wins when it is last; an approval can only be recorded before it. Never a half-applied mix.
            if (rejected)
            {
                status.ShouldBe("REJECTED");
            }
            else
            {
                status.ShouldBe("PENDING_SECOND_APPROVAL");
            }

            steps.ShouldBe(approved ? 1 : 0);
            stepAudits.ShouldBeLessThanOrEqualTo(steps);
        }

        (await _repository.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task Revoke_IsVisibleToOtherConnectionsOnReturn()
    {
        var meta = await _repository.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_table_1"));
        meta.ShouldNotBeNull();
        var consent = await _repository.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TableId = meta.Table.Id,
            TableIdentifier = meta.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-REVOKE-VISIBLE"),
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-5),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        });

        await _repository.RevokeConsentAsync(consent.Id, new Sid("S-1-5-21-DATAOWNER-1"), "no longer needed");

        // A separate connection (not the one that revoked) sees the committed revocation immediately.
        (await ScalarAsync("SELECT is_revoked FROM CONSENTS WHERE id = @id", ("@id", consent.Id.ToString()))).ShouldBe(1);
        (await _repository.GetConsentByIdAsync(consent.Id))!.IsRevoked.ShouldBeTrue();
        (await _repository.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-REVOKE-VISIBLE")], meta.Identifier, DateTimeOffset.UtcNow)).ShouldBeEmpty();
    }
}
