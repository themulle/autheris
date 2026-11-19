using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>ICdcStreamKeyStore</c> (wrapped per-stream data-encryption keys) across PostgreSQL and SQL Server engines.
/// Only wrapped keys are stored. Keys are scoped to a stream, not to a tenant: the isolation cases separate rows by stream id.
/// A concurrent add of the same key has exactly one winner and a retirement is set once.
/// </summary>
public abstract class CdcStreamKeysContract : GovernanceContractBase
{
    private const string Source = "mssql_ct";
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private static readonly TableIdentifier Invoices = new("sales", "dbo", "invoices");

    private static byte[] Fp(int n)
    {
        var b = new byte[32];
        b[0] = (byte)n;
        b[31] = (byte)(n >> 8);
        return b;
    }

    private static CdcProtectedBlob Blob(string dek, byte fill, int length = 40) => new(dek, Enumerable.Repeat(fill, length).ToArray());

    private static CdcStreamEventRecord Ev(CdcStreamId id, long offset, long version, string dek = "dek1") =>
        new(id, offset, version, CdcStreamEventKind.Change, CdcOperation.Insert, CdcTenantScope.Row, "acme", null, Blob(dek, (byte)offset), T0.AddSeconds(offset));

    private static CdcAppendBatch Batch(CdcStreamDescriptor stream, long expectedHead, int count, string dek = "dek1") =>
        new(stream.Id, Source, stream.Table, 1, expectedHead, 100,
            Enumerable.Range(1, count).Select(i => Ev(stream.Id, expectedHead + i, i, dek)).ToList(), []);

    private static CdcWrappedKey Key(CdcStreamId id, string dek, CdcKeyPurpose purpose = CdcKeyPurpose.Payload, string kek = "k1", DateTimeOffset? retired = null) =>
        new(id, dek, purpose, kek, [1, 2, 3, (byte)dek.Length, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], T0, retired);

    [RequiresContainerRuntimeFact]
    public async Task DiscardUnreadable_RemovesEventsImagesAndKeysOfTheDeks_Only()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var keys = Store.CdcStreamKeys;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await keys.TryAddKeyAsync(Key(s.Id, "bad"));
        await keys.TryAddKeyAsync(Key(s.Id, "good"));
        await store.AppendAsync(new CdcAppendBatch(s.Id, Source, Orders, 1, 0, 1,
            [Ev(s.Id, 1, 1, "bad"), Ev(s.Id, 2, 2, "good"), Ev(s.Id, 3, 3, "bad")],
            [new CdcRowImageWrite(Fp(1), CdcTenantScope.Row, "a", Blob("bad", 1), 1), new CdcRowImageWrite(Fp(2), CdcTenantScope.Row, "a", Blob("good", 1), 2)]));

        var removed = await store.DiscardUnreadableAsync(s.Id, ["bad"]);

        removed.ShouldBe(3);
        (await store.ReadAsync(s.Id, 1, 100)).Events.Select(e => e.Offset).ShouldBe([2L]);
        (await store.GetRowImagesAsync(s.Id, [Fp(1), Fp(2)])).Select(i => i.KeyFingerprint[0]).ShouldBe([(byte)2]);
        (await keys.GetKeysAsync(s.Id)).Select(k => k.DekId).ShouldBe(["good"]);
        (await store.DiscardUnreadableAsync(s.Id, [])).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public async Task Keys_RoundTrip_AndSurviveRestart()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var keys = Store.CdcStreamKeys;
        var s = await store.EnsureStreamAsync(Source, Orders);
        var key = Key(s.Id, "dek1", CdcKeyPurpose.Image, "k7");

        (await keys.TryAddKeyAsync(key)).ShouldBeTrue();
        await using var restarted = await Store.CreateAdditionalStoreAsync();
        var loaded = await restarted.CdcStreamKeys.GetKeysAsync(s.Id);

        loaded.Count.ShouldBe(1);
        loaded[0].DekId.ShouldBe("dek1");
        loaded[0].Purpose.ShouldBe(CdcKeyPurpose.Image);
        loaded[0].KekId.ShouldBe("k7");
        loaded[0].WrappedKey.ShouldBe(key.WrappedKey);
        loaded[0].CreatedAt.ShouldBe(T0);
        loaded[0].RetiredAt.ShouldBeNull();
        loaded[0].StreamId.ShouldBe(s.Id);
    }

    [RequiresContainerRuntimeFact]
    public async Task Keys_ConcurrentAddOfTheSameDek_HasExactlyOneWinner()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var keys = Store.CdcStreamKeys;
        var s = await store.EnsureStreamAsync(Source, Orders);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => keys.TryAddKeyAsync(Key(s.Id, "dek1")))));

        results.Count(r => r).ShouldBe(1);
        (await keys.GetKeysAsync(s.Id)).Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task Keys_AreScopedToTheStream()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var keys = Store.CdcStreamKeys;
        var a = await store.EnsureStreamAsync(Source, Orders);
        var b = await store.EnsureStreamAsync(Source, Invoices);
        await keys.TryAddKeyAsync(Key(a.Id, "dek1"));
        (await keys.TryAddKeyAsync(Key(b.Id, "dek1"))).ShouldBeTrue();

        (await keys.GetKeysAsync(a.Id)).Count.ShouldBe(1);
        (await keys.GetKeysAsync(b.Id)).Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task Keys_Retire_SetsOnce_AndReplaceWrappingRewraps()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var keys = Store.CdcStreamKeys;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await keys.TryAddKeyAsync(Key(s.Id, "dek1"));

        await keys.RetireKeyAsync(s.Id, "dek1", T0.AddHours(1));
        await keys.RetireKeyAsync(s.Id, "dek1", T0.AddHours(9));
        await keys.ReplaceWrappingAsync(s.Id, "dek1", "k2", [9, 9, 9]);

        var k = (await keys.GetKeysAsync(s.Id)).Single();
        k.RetiredAt.ShouldBe(T0.AddHours(1));
        k.KekId.ShouldBe("k2");
        k.WrappedKey.ShouldBe([(byte)9, 9, 9]);
    }

    [RequiresContainerRuntimeFact]
    public async Task Keys_DeleteUnreferencedPayloadKeys_ShredsOnlyRetiredUnreferencedPayloadKeys()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var keys = Store.CdcStreamKeys;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await keys.TryAddKeyAsync(Key(s.Id, "old-unused", retired: T0));
        await keys.TryAddKeyAsync(Key(s.Id, "old-used", retired: T0));
        await keys.TryAddKeyAsync(Key(s.Id, "active"));
        await keys.TryAddKeyAsync(Key(s.Id, "image", CdcKeyPurpose.Image, retired: T0));
        await keys.TryAddKeyAsync(Key(s.Id, "fp", CdcKeyPurpose.Fingerprint, retired: T0));
        await store.AppendAsync(Batch(s, 0, 1, dek: "old-used"));

        var removed = await keys.DeleteUnreferencedPayloadKeysAsync(s.Id);

        removed.ShouldBe(1);
        (await keys.GetKeysAsync(s.Id)).Select(k => k.DekId).ShouldBe(["old-used", "active", "image", "fp"], ignoreOrder: true);

        await store.PurgeAsync(s.Id, 2, 10, 1);
        (await keys.DeleteUnreferencedPayloadKeysAsync(s.Id)).ShouldBe(1);
        (await keys.GetKeysAsync(s.Id)).Select(k => k.DekId).ShouldNotContain("old-used");
    }

    [RequiresContainerRuntimeFact]
    public async Task Keys_CountByKek_GroupsAcrossStreams()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var keys = Store.CdcStreamKeys;
        var a = await store.EnsureStreamAsync(Source, Orders);
        var b = await store.EnsureStreamAsync(Source, Invoices);
        await keys.TryAddKeyAsync(Key(a.Id, "d1", kek: "k1"));
        await keys.TryAddKeyAsync(Key(a.Id, "d2", kek: "k2"));
        await keys.TryAddKeyAsync(Key(b.Id, "d1", kek: "k1"));

        var counts = await keys.CountByKekAsync();

        counts["k1"].ShouldBe(2);
        counts["k2"].ShouldBe(1);
        counts.Count.ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var a = await Store.CdcStreams.EnsureStreamAsync(Source, Orders);
        var b = await Store.CdcStreams.EnsureStreamAsync(Source, Invoices);
        await Store.CdcStreamKeys.TryAddKeyAsync(Key(a.Id, "dek1", kek: "ka"));

        (await Store.CdcStreamKeys.GetKeysAsync(b.Id)).ShouldBeEmpty();
        (await Store.CdcStreamKeys.GetKeysAsync(a.Id)).ShouldHaveSingleItem().KekId.ShouldBe("ka");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var a = await Store.CdcStreams.EnsureStreamAsync(Source, Orders);
        var b = await Store.CdcStreams.EnsureStreamAsync(Source, Invoices);
        await Store.CdcStreamKeys.TryAddKeyAsync(Key(a.Id, "dek1", kek: "ka"));
        await Store.CdcStreamKeys.TryAddKeyAsync(Key(b.Id, "dek1", kek: "kb"));

        // The same dek id on stream b: retire, rewrap and shred of b leave the key of stream a untouched.
        await Store.CdcStreamKeys.RetireKeyAsync(b.Id, "dek1", T0.AddHours(1));
        await Store.CdcStreamKeys.ReplaceWrappingAsync(b.Id, "dek1", "k9", [9, 9]);
        await Store.CdcStreamKeys.DeleteUnreferencedPayloadKeysAsync(b.Id);

        var key = (await Store.CdcStreamKeys.GetKeysAsync(a.Id)).Single();
        key.KekId.ShouldBe("ka");
        key.RetiredAt.ShouldBeNull();
        key.WrappedKey.Length.ShouldBe(16);
        (await Store.CdcStreamKeys.GetKeysAsync(b.Id)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        // No tenant concept: the stream id is the fail-closed key; a key without a stream is never stored.
        Should.Throw<ArgumentException>(() => { _ = new CdcStreamId("not-a-stream-id"); });
        await Should.ThrowAsync<Exception>(() => Store.CdcStreamKeys.TryAddKeyAsync(Key(default, "dek1")));
        (await Store.CdcStreamKeys.CountByKekAsync()).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var s = await Store.CdcStreams.EnsureStreamAsync(Source, Orders);

        (await Store.CdcStreamKeys.TryAddKeyAsync(Key(s.Id, "dek1", kek: "k1"))).ShouldBeTrue();
        (await Store.CdcStreamKeys.TryAddKeyAsync(Key(s.Id, "dek1", kek: "k2"))).ShouldBeFalse();

        (await Store.CdcStreamKeys.GetKeysAsync(s.Id)).ShouldHaveSingleItem().KekId.ShouldBe("k1");
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var s = await Store.CdcStreams.EnsureStreamAsync(Source, Orders);
        await Store.CdcStreamKeys.TryAddKeyAsync(Key(s.Id, "dek1"));

        // A duplicate key is reported as false, never as a raw unique violation.
        var act = () => Store.CdcStreamKeys.TryAddKeyAsync(Key(s.Id, "dek1"));

        (await act()).ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var s = await Store.CdcStreams.EnsureStreamAsync(Source, Orders);
        await Store.CdcStreamKeys.TryAddKeyAsync(Key(s.Id, "c") with { CreatedAt = T0.AddSeconds(3) });
        await Store.CdcStreamKeys.TryAddKeyAsync(Key(s.Id, "b") with { CreatedAt = T0.AddSeconds(1) });
        await Store.CdcStreamKeys.TryAddKeyAsync(Key(s.Id, "a") with { CreatedAt = T0.AddSeconds(1) });

        // Ordered by creation time, then dek id.
        (await Store.CdcStreamKeys.GetKeysAsync(s.Id)).Select(k => k.DekId).ShouldBe(["a", "b", "c"]);
        (await Store.CdcStreamKeys.GetKeysAsync(s.Id)).Select(k => k.DekId).ShouldBe(["a", "b", "c"]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        var s = await Store.CdcStreams.EnsureStreamAsync(Source, Orders);

        // No page size exists: the key set of one stream is returned whole.
        for (var i = 0; i < 60; i++)
        {
            (await Store.CdcStreamKeys.TryAddKeyAsync(Key(s.Id, $"dek{i:D3}"))).ShouldBeTrue();
        }

        (await Store.CdcStreamKeys.GetKeysAsync(s.Id)).Count.ShouldBe(60);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var s = await Store.CdcStreams.EnsureStreamAsync(Source, Orders);
        await Store.CdcStreamKeys.TryAddKeyAsync(Key(s.Id, "gone", retired: T0));
        await Store.CdcStreamKeys.TryAddKeyAsync(Key(s.Id, "kept"));

        // A shredded key is physically gone: no read, no count and no re-add race can bring it back as readable material.
        (await Store.CdcStreamKeys.DeleteUnreferencedPayloadKeysAsync(s.Id)).ShouldBe(1);

        (await Store.CdcStreamKeys.GetKeysAsync(s.Id)).Select(k => k.DekId).ShouldBe(["kept"]);
        (await Store.CdcStreamKeys.CountByKekAsync()).Values.Sum().ShouldBe(1);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlCdcStreamKeysContractTests(GovernancePostgresFixture fixture) : CdcStreamKeysContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "cdc_stream_keys_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerCdcStreamKeysContractTests(GovernanceSqlServerFixture fixture) : CdcStreamKeysContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "cdc_stream_keys_contract");
}
