using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Infrastructure.Cache;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>Review E-11 (WORM anchor on separate storage + asymmetric signature) and R2-3 (durable query audit).</summary>
public sealed class AuditAnchorHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-anchor-" + Guid.NewGuid().ToString("N"));

    public AuditAnchorHardeningTests() => Directory.CreateDirectory(_dir);

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

    private static AuditChainAnchor Anchor(long seq, string hash = "h") => new(seq, hash + seq, DateTimeOffset.UtcNow, "hmac");

    private static AsymmetricAuditAnchorSigner NewSigner()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new AsymmetricAuditAnchorSigner(Encoding.UTF8.GetBytes(ec.ExportPkcs8PrivateKeyPem()));
    }

    [Fact]
    public void WormStore_NeverOverwrites_AndLoadsNewest()
    {
        var store = new WormDirectoryAuditChainAnchorStore(Path.Combine(_dir, "worm"));
        store.Load().ShouldBeNull();
        store.Save(Anchor(1));
        store.Save(Anchor(2));
        store.Save(Anchor(3));
        Directory.GetFiles(Path.Combine(_dir, "worm")).Length.ShouldBe(3);
        store.Load()!.Sequence.ShouldBe(3);
    }

    [Fact]
    public void WormStore_ConflictingAnchorsForOneSequence_AreInvalid()
    {
        var store = new WormDirectoryAuditChainAnchorStore(Path.Combine(_dir, "worm"));
        store.Save(Anchor(5, "a"));
        store.Save(new AuditChainAnchor(5, "other", DateTimeOffset.UtcNow.AddSeconds(1), "hmac"));
        store.Load()!.Sequence.ShouldBeLessThan(0);
    }

    [Fact]
    public void Composite_DeletedPrimary_IsRecoveredFromWorm_RolledBackPrimaryIsOutvoted()
    {
        var primary = new FileAuditChainAnchorStore(Path.Combine(_dir, "a.json"));
        var worm = new WormDirectoryAuditChainAnchorStore(Path.Combine(_dir, "worm"));
        var composite = new CompositeAuditChainAnchorStore([primary, worm]);
        composite.Save(Anchor(1));
        composite.Save(Anchor(2));

        File.Delete(Path.Combine(_dir, "a.json"));
        composite.Load()!.Sequence.ShouldBe(2);

        primary.Save(Anchor(1)); // attacker rolls the local file back
        composite.Load()!.Sequence.ShouldBe(2);
    }

    [Fact]
    public void SigningStore_RejectsUnsignedAndForgedAnchors()
    {
        var signer = NewSigner();
        var inner = new InMemoryAuditChainAnchorStore();
        var store = new SigningAuditChainAnchorStore(inner, signer);

        store.Save(Anchor(7));
        store.Load()!.Sequence.ShouldBe(7);

        inner.Save(Anchor(8)); // HMAC-only anchor without KMS signature
        store.Load()!.Sequence.ShouldBeLessThan(0);

        var saved = new InMemoryAuditChainAnchorStore();
        new SigningAuditChainAnchorStore(saved, signer).Save(Anchor(9));
        inner.Save(saved.Load()! with { EntryHash = "forged" });
        store.Load()!.Sequence.ShouldBeLessThan(0);

        // a different key must not verify
        inner.Save(saved.Load()!);
        new SigningAuditChainAnchorStore(inner, NewSigner()).Load()!.Sequence.ShouldBeLessThan(0);
    }

    [Fact]
    public void Signer_VerifyOnlyKey_VerifiesButCannotSign()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var full = new AsymmetricAuditAnchorSigner(Encoding.UTF8.GetBytes(ec.ExportPkcs8PrivateKeyPem()));
        var verifyOnly = new AsymmetricAuditAnchorSigner(null, Encoding.UTF8.GetBytes(ec.ExportSubjectPublicKeyInfoPem()));
        var data = "payload"u8.ToArray();
        verifyOnly.Verify(data, full.Sign(data)).ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => verifyOnly.Sign(data));
        verifyOnly.Verify(data, "zz").ShouldBeFalse();
    }

    private IOptions<GatewayOptions> RepoOptions(string worm, string anchor, bool syncQuery = false) => Options.Create(new GatewayOptions
    {
        GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source={Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db")}", SeedDemoData = false },
        Audit = new AuditOptions { ChainAnchorPath = anchor, ChainAnchorWormDirectory = worm, SynchronousQueryAudit = syncQuery }
    });

    [Fact]
    public async Task Repository_WritesAnchorsToWormDirectory_AndDeletedLocalAnchorDoesNotHideTruncation()
    {
        var worm = Path.Combine(_dir, "worm");
        var anchor = Path.Combine(_dir, "local", "anchor.json");
        var options = RepoOptions(worm, anchor);
        using (var repo = new SqliteGovernanceRepository(new EpochValidationService(), options))
        {
            for (var i = 1; i <= 4; i++)
            {
                await repo.RecordAuditEventAsync(NewEntry(i));
            }
            using var cmd = repo.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM AUDIT_LOG_ENTRIES WHERE rowid IN (SELECT rowid FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC LIMIT 2)";
            await cmd.ExecuteNonQueryAsync();
        }

        Directory.GetFiles(worm).Length.ShouldBeGreaterThanOrEqualTo(4);
        File.Delete(anchor);

        using var restarted = new SqliteGovernanceRepository(new EpochValidationService(), options);
        (await restarted.VerifyAuditHashChainAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task SynchronousQueryAudit_CommitsBeforeReturning()
    {
        var options = RepoOptions(Path.Combine(_dir, "w2"), Path.Combine(_dir, "l2", "a.json"), syncQuery: true);
        using var repo = new SqliteGovernanceRepository(new EpochValidationService(), options);
        await repo.RecordAuditEventAsync(NewEntry(1, "TABLE_QUERY"));

        using var cmd = repo.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES";
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).ShouldBe(1); // no flush of the async channel needed
    }

    [Fact]
    public async Task Repository_TransactionRollback_DoesNotCorruptAuditChainOrTriggerViolation()
    {
        var worm = Path.Combine(_dir, "worm-rb");
        var anchor = Path.Combine(_dir, "local-rb", "anchor.json");
        var options = RepoOptions(worm, anchor, syncQuery: true);
        using var repo = new SqliteGovernanceRepository(new EpochValidationService(), options);

        // 1. Initial valid audit entry
        await repo.RecordAuditEventAsync(NewEntry(1));
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
        var initialWormCount = Directory.Exists(worm) ? Directory.GetFiles(worm).Length : 0;

        // 2. Perform a transaction that rolls back after inserting an audit event
        await repo.LockAsync();
        try
        {
            using var tx = repo.Connection.BeginTransaction();
            await repo.RecordAuditEventInternalAsync(NewEntry(2), CancellationToken.None, tx);
            // Simulate rollback by exiting using block without commit
        }
        finally
        {
            repo.RollbackPendingAuditTransactions();
            repo.ReleaseLock();
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

    private static AuditLogEntry NewEntry(int i, string? eventType = null) => new()
    {
        EventType = eventType ?? $"EVENT_{i}",
        ActorSid = new Sid($"S-1-5-21-ACTOR-{i}"),
        TargetTable = "hr.dbo.salaries",
        Decision = "ALLOW",
        TraceId = $"trace-{i}",
        DetailsJson = "{}"
    };
}
