using System.Data;
using System.Text;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.DialectSql;

public sealed class AuditSqlCoreGoldenTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] TestAnchorKey = Encoding.UTF8.GetBytes("audit-anchor-test-key-32bytes!!");

    public static TheoryData<string> Dialects() => DialectKit.Names();

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task SavePurgeCheckpointAsync_BindsAllParameters(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var checkpoint = new AuditPurgeCheckpoint(
            FirstSurvivingSequence: 100,
            SurvivingPrevHash: "prev-hash-abc",
            PurgedCount: 50,
            PurgedThroughUtc: Now.AddDays(-30),
            CreatedAtUtc: Now,
            Signature: "signature-hex-123",
            ExternalSignature: "kms-ext-sig-456");

        string insertSql = dialect switch
        {
            "PostgreSql" => PostgreSqlGovernanceRepository.AuditSql.InsertPurgeCheckpoint,
            "SqlServer" => SqlServerGovernanceRepository.AuditSql.InsertPurgeCheckpoint,
            _ => SqliteGovernanceRepository.AuditSql.InsertPurgeCheckpoint
        };

        await AuditSqlCore.SavePurgeCheckpointAsync(conn, insertSql, checkpoint, CancellationToken.None);

        conn.Commands.Count.ShouldBe(1);
        var cmd = conn.Commands[0];
        cmd.CommandText.ShouldBe(insertSql);
        cmd.Parameters.Select(p => p.Name).ShouldBe(new[]
        {
            "@id", "@firstSeq", "@prevHash", "@purgedCount", "@purgedThrough", "@createdAt", "@sig", "@extSig"
        });
        cmd.Parameters[1].Value.ShouldBe(100L);
        cmd.Parameters[2].Value.ShouldBe("prev-hash-abc");
        cmd.Parameters[3].Value.ShouldBe(50L);
        cmd.Parameters[4].Value.ShouldBe(Now.AddDays(-30).ToString("O"));
        cmd.Parameters[5].Value.ShouldBe(Now.ToString("O"));
        cmd.Parameters[6].Value.ShouldBe("signature-hex-123");
        cmd.Parameters[7].Value.ShouldBe("kms-ext-sig-456");
    }

    [Fact]
    public void CreateSignedAnchor_And_TryLoadVerifiedAnchor_RoundTrip()
    {
        var clock = new FakeTimeProvider(Now);
        var anchor = AuditSqlCore.CreateSignedAnchor(TestAnchorKey, 42, "hash-0042", clock);

        anchor.Sequence.ShouldBe(42);
        anchor.EntryHash.ShouldBe("hash-0042");
        anchor.UpdatedAt.ShouldBe(Now);
        anchor.Signature.ShouldNotBeNullOrWhiteSpace();

        var store = new InMemoryAuditAnchorStore(anchor);
        var loaded = AuditSqlCore.TryLoadVerifiedAnchor(store, TestAnchorKey, null, out var invalid);

        invalid.ShouldBeFalse();
        loaded.ShouldNotBeNull();
        loaded.Sequence.ShouldBe(42);
        loaded.EntryHash.ShouldBe("hash-0042");
    }

    [Fact]
    public void TryLoadVerifiedAnchor_FailsWhenTampered()
    {
        var clock = new FakeTimeProvider(Now);
        var anchor = AuditSqlCore.CreateSignedAnchor(TestAnchorKey, 42, "hash-0042", clock);
        var tampered = new AuditChainAnchor(42, "hash-TAMPERED", anchor.UpdatedAt, anchor.Signature);

        var store = new InMemoryAuditAnchorStore(tampered);
        var loaded = AuditSqlCore.TryLoadVerifiedAnchor(store, TestAnchorKey, null, out var invalid);

        invalid.ShouldBeTrue();
        loaded.ShouldBeNull();
    }

    [Fact]
    public void GenesisHash_IsStandardFixedValue()
    {
        AuditSqlCore.AuditGenesisHash.ShouldBe("GENESIS_0000000000000000000000000000000000000000000000000000000000000000");
    }

    [Theory]
    [InlineData("same-string", "same-string", true)]
    [InlineData("same-string", "diff-string", false)]
    [InlineData(null, null, true)]
    [InlineData(null, "some", false)]
    [InlineData("some", null, false)]
    public void FixedTimeEqualsString_MatchesExpected(string? a, string? b, bool expected)
    {
        AuditSqlCore.FixedTimeEqualsString(a, b).ShouldBe(expected);
    }

    private sealed class InMemoryAuditAnchorStore(AuditChainAnchor? anchor = null, AuditPurgeCheckpoint? checkpoint = null) : IAuditChainAnchorStore
    {
        public AuditChainAnchor? Load() => anchor;
        public void Save(AuditChainAnchor a) { }
        public AuditPurgeCheckpoint? LoadPurgeCheckpoint() => checkpoint;
        public void SavePurgeCheckpoint(AuditPurgeCheckpoint c) { }
    }
}
