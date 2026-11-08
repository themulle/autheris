using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
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

/// <summary>SR-L-07 residual: anchors never move backwards on any backend.</summary>
[Trait("Category", "Security")]
public sealed class AnchorStoreMonotonicityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-anchor-mono-" + Guid.NewGuid().ToString("N"));

    public AnchorStoreMonotonicityTests() => Directory.CreateDirectory(_dir);

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

    private static AuditChainAnchor Anchor(long seq) => new(seq, "hash-" + seq, DateTimeOffset.UtcNow, "sig");

    [Fact]
    public void FileAnchorStore_Save_LowerSequence_IsIgnored()
    {
        var store = new FileAuditChainAnchorStore(Path.Combine(_dir, "anchor.json"));
        store.Save(Anchor(5));

        store.Save(Anchor(3));

        store.Load()!.Sequence.ShouldBe(5);
    }

    [Fact]
    public void FileAnchorStore_Save_HigherOrEqualSequence_Replaces()
    {
        var store = new FileAuditChainAnchorStore(Path.Combine(_dir, "anchor.json"));
        store.Save(Anchor(5));

        store.Save(Anchor(6));
        store.Load()!.Sequence.ShouldBe(6);

        store.Save(Anchor(6));
        store.Load()!.Sequence.ShouldBe(6);
    }

    [Fact]
    public void FileAnchorStore_Save_ReplacesAnUnparsableFile()
    {
        var path = Path.Combine(_dir, "anchor.json");
        File.WriteAllText(path, "not json");
        var store = new FileAuditChainAnchorStore(path);

        store.Save(Anchor(1));

        store.Load()!.Sequence.ShouldBe(1);
    }

    [Fact]
    public void FileAnchorStore_TwoReplicasOnOnePath_NeverEndBehindTheHighestSequence()
    {
        var path = Path.Combine(_dir, "shared.json");
        var replicaA = new FileAuditChainAnchorStore(path);
        var replicaB = new FileAuditChainAnchorStore(path);

        Parallel.For(1, 41, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var store = i % 2 == 0 ? replicaA : replicaB;
            store.Save(Anchor(i));
        });

        replicaA.Load()!.Sequence.ShouldBe(40);
    }

    [Fact]
    public void InMemoryAnchorStore_Save_LowerSequence_IsIgnored()
    {
        var store = new InMemoryAuditChainAnchorStore();
        store.Save(Anchor(7));

        store.Save(Anchor(2));

        store.Load()!.Sequence.ShouldBe(7);
    }

    private static AuditLogEntry NewEntry(int i) => new()
    {
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        EventType = $"EVENT_{i}",
        ActorSid = new Sid($"S-1-5-21-ACTOR-{i}"),
        TargetTable = "hr.dbo.salaries",
        Decision = "ALLOW",
        TraceId = $"trace-{i}",
        DetailsJson = "{}"
    };
}
