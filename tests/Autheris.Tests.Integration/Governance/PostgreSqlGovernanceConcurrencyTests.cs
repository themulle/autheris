using System.Globalization;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

/// <summary>
/// WP-ARC-B-13 on PostgreSQL (replaces the former SQLite write-gate tests): concurrent writers keep the audit chain sequential and
/// verifiable, and four-eyes approvals stay atomic.
/// </summary>
[Trait("Category", "Security")]
public sealed class PostgreSqlGovernanceConcurrencyTests : IDisposable
{
    private static readonly TableIdentifier FourEyesTable = new("finance", "dbo", "finance_table_5");

    private readonly PostgreSqlGovernanceRepository _repository;
    private readonly string _connectionString;

    public PostgreSqlGovernanceConcurrencyTests()
    {
        _repository = GovernanceTestDatabase.CreateRepository(
            new EpochValidationService(),
            out _connectionString,
            Options.Create(new GatewayOptions
            {
                GovernanceDb = new GovernanceDbOptions { SeedDemoData = true },
                Audit = new AuditOptions { SynchronousQueryAudit = true }
            }));
    }

    public void Dispose() => _repository.Dispose();

    private static AuditLogEntry NewAudit(int i) => new()
    {
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        EventType = "CONCURRENT_ADMIN_ACTION",
        ActorSid = new Sid($"S-1-5-21-WRITER-{i % 7}"),
        TargetTable = "finance.dbo.finance_table_1",
        TraceId = $"trace-{i:D5}",
        DetailsJson = $"{{\"i\":{i}}}"
    };

    private static ConsentRequest NewRequest(TableMetadata meta, string requester) => new()
    {
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        TableId = meta.Table.Id,
        TableIdentifier = FourEyesTable,
        RequesterSid = new Sid(requester),
        RequestedGranteeType = GranteeType.User,
        RequestedGranteeRef = requester,
        BusinessJustification = "Concurrency test",
        RequestedValidTo = TimeProvider.System.GetUtcNow().AddDays(7)
    };

    private async Task<ConsentRequest> NewFourEyesRequestAsync(string requester)
    {
        var meta = await _repository.GetTableMetadataAsync(FourEyesTable);
        meta.ShouldNotBeNull();
        return await _repository.CreateConsentRequestAsync(NewRequest(meta, requester));
    }

    private long Scalar(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt64(GovernanceTestDatabase.ScalarRaw(_connectionString, sql, parameters), CultureInfo.InvariantCulture);

    private static async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [Fact]
    public async Task AuditChain_ConcurrentWriters_RemainSequentialAndVerifiable()
    {
        const int writers = 8;
        const int perWriter = 25;

        var auditWriters = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                await _repository.RecordAuditEventAsync(NewAudit((w * perWriter) + i));
            }
        }));

        // Business transactions audit inside their own transaction and advance the same chain tail.
        var approvals = Enumerable.Range(0, 6).Select(n => Task.Run(async () =>
        {
            var request = await NewFourEyesRequestAsync($"S-1-5-21-REQ-CHAIN-{n}");
            await _repository.ApproveConsentRequestStepAsync(request.Id, new Sid("S-1-5-21-APPROVER-A"));
        }));

        await Task.WhenAll(auditWriters.Concat(approvals));

        (await _repository.VerifyAuditHashChainAsync()).ShouldBeTrue();

        var total = Scalar("SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES");
        total.ShouldBeGreaterThanOrEqualTo(writers * perWriter + 6);
        Scalar("SELECT MAX(seq) FROM AUDIT_LOG_ENTRIES").ShouldBe(total, "the sequence is contiguous: max equals the row count");
        Scalar("SELECT COUNT(DISTINCT seq) FROM AUDIT_LOG_ENTRIES").ShouldBe(total);
    }

    [Fact]
    public async Task ConcurrentApprovalsBySameApprover_RecordExactlyOneStep()
    {
        var request = await NewFourEyesRequestAsync("S-1-5-21-REQ-SAME-APPROVER");
        var approver = new Sid("S-1-5-21-APPROVER-A");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            TryAsync(() => _repository.ApproveConsentRequestStepAsync(request.Id, approver)))));

        attempts.Count(succeeded => succeeded).ShouldBe(1, "the four-eyes check and its write are atomic");
        Scalar("SELECT COUNT(*) FROM APPROVAL_STEPS WHERE consent_request_id = @id", ("@id", request.Id.ToString())).ShouldBe(1);
        (await _repository.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        (await _repository.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task ConcurrentApprovalsByTwoApprovers_NeverCompleteWithoutTwoDistinctSteps()
    {
        var request = await NewFourEyesRequestAsync("S-1-5-21-REQ-TWO-APPROVERS");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(n => Task.Run(() =>
                TryAsync(() => _repository.ApproveConsentRequestStepAsync(request.Id, new Sid(n % 2 == 0 ? "S-1-5-21-APPROVER-A" : "S-1-5-21-APPROVER-B"))))));

        results.Count(r => r).ShouldBe(2, "exactly one step per distinct approver");
        Scalar("SELECT COUNT(DISTINCT approver_sid) FROM APPROVAL_STEPS WHERE consent_request_id = @id", ("@id", request.Id.ToString())).ShouldBe(2);
    }

    [Fact]
    public async Task ConcurrentApproveAndReject_EndInOneConsistentState()
    {
        for (var round = 0; round < 5; round++)
        {
            var request = await NewFourEyesRequestAsync($"S-1-5-21-REQ-RACE-{round}");

            var approve = Task.Run(() => TryAsync(() => _repository.ApproveConsentRequestStepAsync(request.Id, new Sid("S-1-5-21-APPROVER-A"))));
            var reject = Task.Run(() => TryAsync(() => _repository.RejectConsentRequestAsync(request.Id, new Sid("S-1-5-21-APPROVER-B"), "race")));
            var approved = await approve;
            var rejected = await reject;

            var status = (await _repository.GetConsentRequestAsync(request.Id))!.Status;
            var steps = Scalar("SELECT COUNT(*) FROM APPROVAL_STEPS WHERE consent_request_id = @id AND decision = 'APPROVED'", ("@id", request.Id.ToString()));
            var stepAudits = Scalar(
                "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE event_type = 'CONSENT_APPROVAL_STEP' AND details_json LIKE @needle",
                ("@needle", $"%{request.Id}%"));

            // A rejection always wins when it is last; an approval can only be recorded before it. Never a half-applied mix.
            status.ShouldBe(rejected ? "REJECTED" : "PENDING_SECOND_APPROVAL");
            steps.ShouldBe(approved ? 1 : 0);
            stepAudits.ShouldBe(steps);
        }

        (await _repository.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task Revoke_IsVisibleToOtherConnectionsOnReturn()
    {
        var meta = await _repository.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_table_1"));
        meta.ShouldNotBeNull();
        var now = TimeProvider.System.GetUtcNow();
        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            Id = Guid.NewGuid(),
            TableId = meta.Table.Id,
            TableIdentifier = meta.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-REVOKE-VISIBLE"),
            ValidFrom = now.AddMinutes(-5),
            ValidTo = now.AddDays(1)
        });

        await _repository.RevokeConsentAsync(consent.Id, new Sid("S-1-5-21-DATAOWNER-1"), "no longer needed");

        // A separate connection (not the one that revoked) sees the committed revocation immediately.
        Scalar("SELECT is_revoked FROM CONSENTS WHERE id = @id", ("@id", consent.Id.ToString())).ShouldBe(1);
        (await _repository.GetConsentByIdAsync(consent.Id))!.IsRevoked.ShouldBeTrue();
        (await _repository.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-REVOKE-VISIBLE")], meta.Identifier, now)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AuditPipeline_TierBEvents_AreWrittenAndFlushOnRead()
    {
        for (var i = 0; i < 30; i++)
        {
            await _repository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = Autheris.Tests.Common.TestTenants.Default,
                EventType = AuditEventTypes.TableQuery,
                ActorSid = new Sid("S-1-5-21-READER"),
                TargetTable = "finance.dbo.finance_table_1",
                TraceId = $"tierb-{i}"
            });
        }

        var entries = await _repository.QueryAuditLogsAsync(actorSid: new Sid("S-1-5-21-READER"));

        entries.Count.ShouldBe(30);
        (await _repository.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task AuditChain_TierBChannel_WithApprovalsFlushAndVerify_RemainsSequentialAndVerifiable()
    {
        // The Tier-B channel (SynchronousQueryAudit=false) writes its batches from the background processor.
        using var repository = GovernanceTestDatabase.CreateRepository(
            new EpochValidationService(),
            Options.Create(new GatewayOptions
            {
                GovernanceDb = new GovernanceDbOptions { SeedDemoData = true },
                Audit = new AuditOptions { SynchronousQueryAudit = false }
            }));
        var meta = await repository.GetTableMetadataAsync(FourEyesTable);
        meta.ShouldNotBeNull();

        const int writers = 4;
        const int perWriter = 30;
        var tierB = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                await repository.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = Autheris.Tests.Common.TestTenants.Default,
                    EventType = AuditEventTypes.TableQuery,
                    ActorSid = new Sid($"S-1-5-21-CHANNEL-{w}"),
                    TargetTable = "finance.dbo.finance_table_1",
                    TraceId = $"channel-{w}-{i}"
                });
            }
        }));
        var approvals = Enumerable.Range(0, 5).Select(n => Task.Run(async () =>
        {
            var request = await repository.CreateConsentRequestAsync(NewRequest(meta, $"S-1-5-21-REQ-CHANNEL-{n}"));
            await repository.ApproveConsentRequestStepAsync(request.Id, new Sid("S-1-5-21-APPROVER-A"));
        }));
        var flushAndVerify = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                await repository.FlushAuditChannelAsync();
                (await repository.VerifyAuditHashChainAsync()).ShouldBeTrue();
            }
        });

        await Task.WhenAll(tierB.Concat(approvals).Append(flushAndVerify));
        await repository.FlushAuditChannelAsync();

        (await repository.VerifyAuditHashChainAsync()).ShouldBeTrue();
        var entries = await repository.QueryAuditLogsAsync(limit: 5000);
        entries.Count(e => e.EventType == AuditEventTypes.TableQuery).ShouldBe(writers * perWriter);
    }

    [Fact]
    public async Task RecordAuditInCallerTransaction_RolledBack_LeavesNoRowAndKeepsTheChainVerifiable()
    {
        // PostgreSQL accepts a caller transaction by design (the chain tail advances only on commit); the SQLite "foreign transaction is rejected" rule does not apply.
        await using var conn = new Npgsql.NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var before = Scalar("SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES");

        await _repository.RecordAuditEventAsync(NewAudit(1), tx);
        await tx.RollbackAsync();
        _repository.OnTransactionRolledBack(tx);

        Scalar("SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES").ShouldBe(before);
        await _repository.RecordAuditEventAsync(NewAudit(2));
        (await _repository.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task MetadataReader_WithSnapshotBeforeACommit_DoesNotServeStaleMetadataAfterwards()
    {
        var table = new TableIdentifier("sales", "dbo", "cache_race");
        Table Entity(string description) => new()
        {
            SourceType = "sql",
            SourceName = "sales",
            SchemaName = "dbo",
            TableName = "cache_race",
            DisplayName = "Cache race",
            Description = description,
            DataSourceType = DataSourceType.Sql
        };
        await _repository.UpsertTableMetadataAsync(new TableMetadata { Identifier = table, Table = Entity("old"), Columns = [] });
        _repository.InvalidateMetadataCacheForTesting();

        // Make the next metadata read stall after it loaded its snapshot and before it is cached.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _repository.AfterMetadataSnapshotForTesting = async _ =>
        {
            _repository.AfterMetadataSnapshotForTesting = null;
            reached.SetResult();
            await release.Task;
        };
        var staleReader = Task.Run(() => _repository.GetTableMetadataAsync(table));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A commit (which bumps the table epoch) happens while the reader still holds the old snapshot.
        await _repository.UpsertTableMetadataAsync(new TableMetadata { Identifier = table, Table = Entity("new"), Columns = [] });
        release.SetResult();
        (await staleReader)!.Table.Description.ShouldBe("old", "the stalled reader legitimately saw the old snapshot");

        (await _repository.GetTableMetadataAsync(table))!.Table.Description.ShouldBe("new", "a cached stale snapshot must be refused by the epoch check");
    }
}
