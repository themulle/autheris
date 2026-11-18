using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>Contract suite for <c>IDocLinkRepository</c> across PostgreSQL and SQL Server engines.</summary>
public abstract class DocLinkContract : GovernanceContractBase
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    private IDocLinkRepository Links => Store.Services.GetRequiredService<IDocLinkRepository>();

    private IDocumentRepository Docs => Store.Services.GetRequiredService<IDocumentRepository>();

    private static DocLinkTarget Table(string name) => new(DocLinkTargetKind.Table, "sales", "dbo", name);

    private static DocLinkSource Source(string tenant, string docId = "d1") => new(DocLinkSourceKind.Document, "app-a", DocCategory.Guide, docId, tenant);

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Links.ReplaceForSourceAsync(Store.TenantA.Value, Source(Store.TenantA.Value), [Table("orders")], "S-1");
        await Links.ReplaceForSourceAsync(Store.TenantB.Value, Source(Store.TenantB.Value), [Table("orders")], "S-2");

        (await Links.ListForTargetsAsync(Store.TenantA.Value, [Table("orders")])).Should().ContainSingle().Which.TenantId.Should().Be(Store.TenantA.Value);
        (await Links.ListForSourceAsync(Store.TenantA.Value, Source(Store.TenantB.Value))).Should().BeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Links_TenantScoped()
    {
        if (!Store.IsAvailable) return;
        var official = new DocLinkSource(DocLinkSourceKind.Document, "app-o", DocCategory.Guide, "o1", null);
        await Links.ReplaceForSourceAsync(Store.TenantA.Value, official, [Table("orders")], "S-1");
        await Links.ReplaceForSourceAsync(Store.TenantB.Value, official, [Table("invoices")], "S-2");

        (await Links.ListForSourceAsync(Store.TenantA.Value, official)).Select(l => l.Target.Name).Should().Equal("orders");
        (await Links.DeleteForSourceAsync(Store.TenantA.Value, official)).Should().Be(1);
        (await Links.ListForSourceAsync(Store.TenantB.Value, official)).Should().ContainSingle();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var source = Source(Store.TenantA.Value);
        await Links.ReplaceForSourceAsync(Store.TenantA.Value, source, [Table("orders")], "S-1");
        await Links.ReplaceForSourceAsync(Store.TenantB.Value, source, [Table("other")], "S-2");
        await Links.DeleteForSourceAsync(Store.TenantB.Value, source);

        (await Links.ListForSourceAsync(Store.TenantA.Value, source)).Should().ContainSingle();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        var act = () => Links.ReplaceForSourceAsync(string.Empty, Source("t"), [Table("orders")], "S-1");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var source = Source(Store.TenantA.Value);
        await Links.ReplaceForSourceAsync(Store.TenantA.Value, source, [Table("orders"), Table("orders")], "S-1");
        await Links.ReplaceForSourceAsync(Store.TenantA.Value, source, [Table("orders")], "S-1");

        (await Links.ListForSourceAsync(Store.TenantA.Value, source)).Should().ContainSingle();
    }

    [RequiresContainerRuntimeFact]
    public async Task ReplaceForSource_IsAtomic()
    {
        if (!Store.IsAvailable) return;
        var source = Source(Store.TenantA.Value);
        await Links.ReplaceForSourceAsync(Store.TenantA.Value, source, [Table("orders")], "S-1");

        var tooMany = Enumerable.Range(0, DocLinkLimits.MaxTargetsPerSource + 1).Select(i => Table($"t{i}")).ToList();
        var act = () => Links.ReplaceForSourceAsync(Store.TenantA.Value, source, tooMany, "S-1");
        await act.Should().ThrowAsync<ArgumentException>();

        (await Links.ListForSourceAsync(Store.TenantA.Value, source)).Select(l => l.Target.Name).Should().Equal("orders");
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var source = Source(Store.TenantA.Value);
        var column = new DocLinkTarget(DocLinkTargetKind.Column, "sales", "dbo", "orders", "id");
        await Links.ReplaceForSourceAsync(Store.TenantA.Value, source, [Table("orders"), column, column], "S-1");

        (await Links.ListForSourceAsync(Store.TenantA.Value, source)).Should().HaveCount(2);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var source = Source(Store.TenantA.Value);
        await Links.ReplaceForSourceAsync(Store.TenantA.Value, source, [Table("b"), Table("a"), Table("c")], "S-1");

        (await Links.ListForSourceAsync(Store.TenantA.Value, source)).Select(l => l.Target.Name).Should().Equal("a", "b", "c");
    }

    [RequiresContainerRuntimeFact]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        var tooMany = Enumerable.Range(0, DocLinkLimits.MaxTargetsPerSource + 1).Select(i => Table($"t{i}")).ToList();
        var act = () => Links.ListForTargetsAsync(Store.TenantA.Value, tooMany);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var doc = new DocRecord(Store.TenantA.Value, "app-a", DocCategory.Guide, "d-link", "Title", "# Body", "sha", 6, [], null, "S-1", 1, T0);
        (await Docs.CreateAsync(doc, new DocQuota(10, 100_000))).Status.Should().Be(DocWriteStatus.Created);
        var source = Source(Store.TenantA.Value, "d-link");
        await Links.ReplaceForSourceAsync(Store.TenantA.Value, source, [Table("orders")], "S-1");

        (await Docs.SetDiscoveryHiddenAsync(new DocKey(Store.TenantA.Value, "app-a", DocCategory.Guide, "d-link"), true, T0.AddMinutes(1))).Should().Be(false);
        (await Docs.GetAsync(new DocKey(Store.TenantA.Value, "app-a", DocCategory.Guide, "d-link")))!.DiscoveryHidden.Should().BeTrue();

        (await Docs.DeleteAsync(new DocKey(Store.TenantA.Value, "app-a", DocCategory.Guide, "d-link"), null, T0.AddMinutes(2))).Status.Should().Be(DocWriteStatus.Deleted);
        (await Links.ListForSourceAsync(Store.TenantA.Value, source)).Should().BeEmpty();
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlDocLinkContractTests(GovernancePostgresFixture fixture) : DocLinkContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "doc_link_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerDocLinkContractTests(GovernanceSqlServerFixture fixture) : DocLinkContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "doc_link_contract");
}
