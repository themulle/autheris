using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>Contract suite for <c>IDocumentRepository</c> across PostgreSQL and SQL Server engines.</summary>
public abstract class DocumentContract : GovernanceContractBase
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DocQuota DefaultQuota = new(10, 100_000);

    private IDocumentRepository Docs => Store.Services.GetRequiredService<IDocumentRepository>();

    private static DocRecord NewDoc(string? tenantId, string appId, string docId, string content = "# Hello\nBody", DateTimeOffset? updatedAt = null) =>
        new(tenantId, appId, DocCategory.Guide, docId, "Title " + docId, content, "sha-" + docId, content.Length, ["tag1"], null, "S-1-5-21-1-1", 1, updatedAt ?? T0);

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Docs.CreateAsync(NewDoc(Store.TenantA.Value, "app-a", "d1"), DefaultQuota);
        await Docs.CreateAsync(NewDoc(Store.TenantB.Value, "app-a", "d2"), DefaultQuota);

        (await Docs.GetAsync(new DocKey(Store.TenantA.Value, "app-a", DocCategory.Guide, "d1"))).Should().NotBeNull();
        (await Docs.GetAsync(new DocKey(Store.TenantA.Value, "app-a", DocCategory.Guide, "d2"))).Should().BeNull();
        (await Docs.GetAsync(new DocKey(Store.TenantB.Value, "app-a", DocCategory.Guide, "d1"))).Should().BeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var aDoc = NewDoc(Store.TenantA.Value, "app-iso", "same-id", "tenant-a");
        var bDoc = NewDoc(Store.TenantB.Value, "app-iso", "same-id", "tenant-b");

        (await Docs.CreateAsync(aDoc, DefaultQuota)).Status.Should().Be(DocWriteStatus.Created);
        (await Docs.CreateAsync(bDoc, DefaultQuota)).Status.Should().Be(DocWriteStatus.Created);
        (await Docs.ReplaceAsync(bDoc with { Content = "updated-b", ByteCount = 9 }, 1, DefaultQuota)).Status.Should().Be(DocWriteStatus.Replaced);

        var loadedA = await Docs.GetAsync(new DocKey(Store.TenantA.Value, "app-iso", DocCategory.Guide, "same-id"));
        loadedA!.Content.Should().Be("tenant-a");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        var emptyCreate = () => Docs.CreateAsync(NewDoc(string.Empty, "app-x", "d1"), DefaultQuota);
        await emptyCreate.Should().ThrowAsync<ArgumentException>();
        var emptyPurge = () => Docs.PurgeTenantAsync("   ");
        await emptyPurge.Should().ThrowAsync<ArgumentException>();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var official = NewDoc(null, "app-off", "official-1");
        (await Docs.CreateAsync(official, DefaultQuota)).Status.Should().Be(DocWriteStatus.Created);
        (await Docs.CreateAsync(official, DefaultQuota)).Status.Should().Be(DocWriteStatus.AlreadyExists);
        (await Docs.CreateAsync(NewDoc(Store.TenantA.Value, "app-off", "official-1"), DefaultQuota)).Status.Should().Be(DocWriteStatus.ReservedKey);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var calls = Enumerable.Range(0, 4).Select(_ => Docs.CreateAsync(NewDoc(Store.TenantA.Value, "app-race", "race-1"), DefaultQuota));
        var results = await Task.WhenAll(calls);
        results.Count(r => r.Status == DocWriteStatus.Created).Should().Be(1);
        results.Count(r => r.Status == DocWriteStatus.AlreadyExists).Should().Be(3);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        await Docs.CreateAsync(NewDoc(Store.TenantA.Value, "app-chg", "c1", updatedAt: T0.AddSeconds(1)), DefaultQuota);
        await Docs.CreateAsync(NewDoc(Store.TenantA.Value, "app-chg", "c2", updatedAt: T0.AddSeconds(2)), DefaultQuota);
        var changed = await Docs.ReadChangedSinceAsync(T0.AddSeconds(2));
        changed.Select(d => d.DocId).Should().Contain("c2").And.NotContain("c1");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        var tightQuota = new DocQuota(2, 20);
        var batch = await Docs.CreateManyAsync([
            NewDoc(Store.TenantA.Value, "app-q", "q1", "1234567890"),
            NewDoc(Store.TenantA.Value, "app-q", "q2", "12345678901")
        ], tightQuota);
        batch.Status.Should().Be(DocWriteStatus.QuotaExceeded);
        (await Docs.GetAsync(new DocKey(Store.TenantA.Value, "app-q", DocCategory.Guide, "q1"))).Should().BeNull();
        (await Docs.GetTenantUsageAsync(Store.TenantA.Value)).DocCount.Should().Be(0);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var doc = NewDoc(Store.TenantA.Value, "app-del", "d-tomb", "12345");
        var created = await Docs.CreateAsync(doc, DefaultQuota);
        created.Status.Should().Be(DocWriteStatus.Created);

        (await Docs.ReplaceAsync(doc with { Content = "bad" }, expectedVersion: 99, DefaultQuota)).Status.Should().Be(DocWriteStatus.VersionMismatch);
        (await Docs.DeleteAsync(new DocKey(Store.TenantA.Value, "app-del", DocCategory.Guide, "d-tomb"), 99, T0.AddMinutes(1))).Status.Should().Be(DocWriteStatus.VersionMismatch);
        (await Docs.DeleteAsync(new DocKey(Store.TenantA.Value, "app-del", DocCategory.Guide, "d-tomb"), 1, T0.AddMinutes(1))).Status.Should().Be(DocWriteStatus.Deleted);

        (await Docs.GetAsync(new DocKey(Store.TenantA.Value, "app-del", DocCategory.Guide, "d-tomb"))).Should().BeNull();
        (await Docs.GetTenantUsageAsync(Store.TenantA.Value)).DocCount.Should().Be(0);
        (await Docs.PurgeTombstonesAsync(T0.AddMinutes(2))).Should().BeGreaterOrEqualTo(1);

        await Docs.CreateAsync(NewDoc(Store.TenantB.Value, "app-del", "b1"), DefaultQuota);
        (await Docs.PurgeTenantAsync(Store.TenantB.Value)).Should().BeGreaterOrEqualTo(1);
        (await Docs.GetAsync(new DocKey(Store.TenantB.Value, "app-del", DocCategory.Guide, "b1"))).Should().BeNull();
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlDocumentContractTests(GovernancePostgresFixture fixture) : DocumentContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "document_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerDocumentContractTests(GovernanceSqlServerFixture fixture) : DocumentContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "document_contract");
}
