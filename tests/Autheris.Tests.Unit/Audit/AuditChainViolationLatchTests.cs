using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>SR-L-08 / decision G-5: a detected audit chain violation latches and is applied per <see cref="AuditChainViolationPolicy"/>.</summary>
[Trait("Category", "Security")]
public sealed class AuditChainViolationLatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-latch-" + Guid.NewGuid().ToString("N"));

    public AuditChainViolationLatchTests() => Directory.CreateDirectory(_dir);

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

    private SqliteGovernanceRepository CreateRepo(AuditChainViolationPolicy? policy)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        var db = Path.Combine(_dir, $"audit_{Guid.NewGuid():N}.db");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source={db}", SeedDemoData = false },
            Audit = new AuditOptions { ChainAnchorPath = Path.Combine(_dir, Path.GetFileName(db) + ".anchor.json"), OnChainViolation = policy }
        });
        return GovernanceStoreFactory.CreateSqlite(new EpochValidationService(), options, env);
    }

    private static AuditLogEntry TierA(int i) => new()
    {
        EventType = AuditEventTypes.AuditConfigChanged,
        ActorSid = new Sid($"S-1-5-21-ADMIN-{i}"),
        TargetTable = "governance",
        Decision = "ALLOW",
        TraceId = $"trace-a-{i}",
        DetailsJson = "{\"change\":" + i + "}"
    };

    private static AuditLogEntry TierB(int i) => new()
    {
        EventType = AuditEventTypes.TableQuery,
        ActorSid = new Sid($"S-1-5-21-USER-{i}"),
        TargetTable = "hr.dbo.salaries",
        Decision = "ALLOW",
        TraceId = $"trace-b-{i}",
        DetailsJson = "{}"
    };

    /// <summary>Writes a few events, tampers with the first row and lets the verifier latch the violation.</summary>
    private static async Task LatchViolationAsync(SqliteGovernanceRepository repo)
    {
        await repo.RecordAuditEventAsync(TierA(1));
        await repo.RecordAuditEventAsync(TierA(2));
        using (var cmd = repo.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE AUDIT_LOG_ENTRIES SET details_json = '{\"tampered\":true}' WHERE rowid = 1";
            await cmd.ExecuteNonQueryAsync();
        }

        (await repo.VerifyAuditHashChainAsync()).ShouldBeFalse();
        repo.IsAuditPipelineFaulted.ShouldBeTrue();
    }

    [Fact]
    public async Task TierB_AfterViolationFlagged_AndSuccessfulBatch_StillRejected()
    {
        using var repo = CreateRepo(AuditChainViolationPolicy.DenyDataAccess);
        await LatchViolationAsync(repo);

        // A successful Tier-A write (the batch writer path that used to reset the fault flag) must not re-enable query admission.
        await repo.RecordAuditEventAsync(TierA(3));
        await repo.FlushAuditChannelAsync();

        await Should.ThrowAsync<AuditUnavailableException>(() => repo.RecordAuditEventAsync(TierB(1)));
        repo.IsAuditPipelineFaulted.ShouldBeTrue();
    }

    [Fact]
    public async Task OnChainViolation_DenyDataAccess_MarksTierA_AndWritesOneFaultEvent()
    {
        using var repo = CreateRepo(AuditChainViolationPolicy.DenyDataAccess);
        await LatchViolationAsync(repo);

        await repo.RecordAuditEventAsync(TierA(3));
        await repo.RecordAuditEventAsync(TierA(4));

        var entries = await repo.GetAuditLogEntriesAsync(100);
        var marked = entries.Where(e => e.EventType == AuditEventTypes.AuditConfigChanged && e.TraceId is "trace-a-3" or "trace-a-4").ToList();
        marked.Count.ShouldBe(2);
        foreach (var e in marked)
        {
            using var doc = JsonDocument.Parse(e.DetailsJson);
            doc.RootElement.GetProperty("chainViolation").GetBoolean().ShouldBeTrue();
        }

        entries.Count(e => e.EventType == AuditEventTypes.AuditPipelineFault).ShouldBe(1);
    }

    [Fact]
    public async Task OnChainViolation_DenyAll_RejectsTierA()
    {
        using var repo = CreateRepo(AuditChainViolationPolicy.DenyAll);
        await LatchViolationAsync(repo);

        await Should.ThrowAsync<AuditUnavailableException>(() => repo.RecordAuditEventAsync(TierA(3)));
        await Should.ThrowAsync<AuditUnavailableException>(() => repo.RecordAuditEventAsync(TierB(1)));
    }

    [Fact]
    public async Task OnChainViolation_AlertOnly_AllowsQueries()
    {
        using var repo = CreateRepo(AuditChainViolationPolicy.AlertOnly);
        await LatchViolationAsync(repo);

        await Should.NotThrowAsync(() => repo.RecordAuditEventAsync(TierB(1)));
        await Should.NotThrowAsync(() => repo.RecordAuditEventAsync(TierA(3)));
    }

    [Fact]
    public void ResolvePolicy_DefaultsToDenyDataAccessOutsideDevelopment_AndAlertOnlyInDevelopment()
    {
        AuditChainViolationGate.ResolvePolicy(new AuditOptions(), isDevOrTest: false).ShouldBe(AuditChainViolationPolicy.DenyDataAccess);
        AuditChainViolationGate.ResolvePolicy(new AuditOptions(), isDevOrTest: true).ShouldBe(AuditChainViolationPolicy.AlertOnly);
        AuditChainViolationGate.ResolvePolicy(new AuditOptions { OnChainViolation = AuditChainViolationPolicy.DenyAll }, isDevOrTest: true)
            .ShouldBe(AuditChainViolationPolicy.DenyAll);
    }
}
