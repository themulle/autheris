using System;
using System.IO;
using System.Security.Cryptography;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Infrastructure.Persistence;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

[Trait("Category", "Security")]
public sealed class AuditPurgeCheckpointAnchorStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-purge-anchor-" + Guid.NewGuid().ToString("N"));

    public AuditPurgeCheckpointAnchorStoreTests() => Directory.CreateDirectory(_dir);

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

    private static AuditPurgeCheckpoint Checkpoint(long firstSurvivingSeq, string prevHash = "prev-hash-test")
    {
        var now = DateTimeOffset.UtcNow;
        return new AuditPurgeCheckpoint(
            FirstSurvivingSequence: firstSurvivingSeq,
            SurvivingPrevHash: prevHash,
            PurgedCount: firstSurvivingSeq - 1,
            PurgedThroughUtc: now.AddDays(-90),
            CreatedAtUtc: now,
            Signature: "test-sig-" + firstSurvivingSeq,
            ExternalSignature: null);
    }

    [Fact]
    public void InMemoryStore_SaveAndLoad_Roundtrips()
    {
        var store = new InMemoryAuditChainAnchorStore();
        store.LoadPurgeCheckpoint().ShouldBeNull();

        var cp = Checkpoint(100);
        store.SavePurgeCheckpoint(cp);

        var loaded = store.LoadPurgeCheckpoint();
        loaded.ShouldNotBeNull();
        loaded.FirstSurvivingSequence.ShouldBe(100);
        loaded.SurvivingPrevHash.ShouldBe("prev-hash-test");
        loaded.PurgedCount.ShouldBe(99);
    }

    [Fact]
    public void InMemoryStore_MonotonicProgression_LowerSequenceIgnored()
    {
        var store = new InMemoryAuditChainAnchorStore();
        store.SavePurgeCheckpoint(Checkpoint(100));

        // Regressive sequence attempt must fail-closed per SEC-AUD161-02
        Should.Throw<InvalidOperationException>(() => store.SavePurgeCheckpoint(Checkpoint(50)));

        store.LoadPurgeCheckpoint()!.FirstSurvivingSequence.ShouldBe(100);
    }

    [Fact]
    public void FileStore_SaveAndLoad_Roundtrips()
    {
        var path = Path.Combine(_dir, "anchor.json");
        var store = new FileAuditChainAnchorStore(path);

        var cp = Checkpoint(500);
        store.SavePurgeCheckpoint(cp);

        var loaded = store.LoadPurgeCheckpoint();
        loaded.ShouldNotBeNull();
        loaded.FirstSurvivingSequence.ShouldBe(500);
        loaded.Signature.ShouldBe("test-sig-500");
    }

    [Fact]
    public void FileStore_MonotonicProgression_LowerSequenceIgnored()
    {
        var path = Path.Combine(_dir, "anchor.json");
        var store = new FileAuditChainAnchorStore(path);

        store.SavePurgeCheckpoint(Checkpoint(500));

        // Regressive sequence attempt must fail-closed per SEC-AUD161-02
        Should.Throw<InvalidOperationException>(() => store.SavePurgeCheckpoint(Checkpoint(200)));

        store.LoadPurgeCheckpoint()!.FirstSurvivingSequence.ShouldBe(500);
    }

    [Fact]
    public void WormStore_WritesImmutableFiles_AndLoadsNewest()
    {
        var wormDir = Path.Combine(_dir, "worm");
        var store = new WormDirectoryAuditChainAnchorStore(wormDir);

        store.SavePurgeCheckpoint(Checkpoint(100));
        store.SavePurgeCheckpoint(Checkpoint(200));

        var files = Directory.GetFiles(wormDir, "purge-*.json");
        files.Length.ShouldBe(2);

        var loaded = store.LoadPurgeCheckpoint();
        loaded.ShouldNotBeNull();
        loaded.FirstSurvivingSequence.ShouldBe(200);
    }

    [Fact]
    public void CompositeStore_FansOutPurgeCheckpoints()
    {
        var primary = new InMemoryAuditChainAnchorStore();
        var secondary = new InMemoryAuditChainAnchorStore();
        var composite = new CompositeAuditChainAnchorStore(primary, secondary);

        var cp = Checkpoint(300);
        composite.SavePurgeCheckpoint(cp);

        primary.LoadPurgeCheckpoint()!.FirstSurvivingSequence.ShouldBe(300);
        secondary.LoadPurgeCheckpoint()!.FirstSurvivingSequence.ShouldBe(300);
        composite.LoadPurgeCheckpoint()!.FirstSurvivingSequence.ShouldBe(300);
    }

    private sealed class TestAnchorSigner(string signature) : IAuditAnchorSigner
    {
        public string Sign(ReadOnlySpan<byte> data) => signature;
        public bool Verify(ReadOnlySpan<byte> data, string sig) => string.Equals(sig, signature, StringComparison.Ordinal);
    }

    [Fact]
    public void SigningStore_DecoratesWithKmsSignature()
    {
        var inner = new InMemoryAuditChainAnchorStore();
        var signer = new TestAnchorSigner("kms-external-signature-hex");

        var signingStore = new SigningAuditChainAnchorStore(inner, signer);

        var cp = Checkpoint(1000);
        cp.ExternalSignature.ShouldBeNull();

        signingStore.SavePurgeCheckpoint(cp);

        var loaded = signingStore.LoadPurgeCheckpoint();
        loaded.ShouldNotBeNull();
        loaded.FirstSurvivingSequence.ShouldBe(1000);
        loaded.ExternalSignature.ShouldBe("kms-external-signature-hex");
    }
}
