using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #111 review: the per-row overlay does not allocate for tables without tags and sorts once.</summary>
public sealed class DatasetCatalogStoreOverlayTests
{
    [Fact]
    public void Get_RowsWithoutTags_ShareOneEmptyArray()
    {
        var set = new DatasetCatalogStore.OverlaySet();

        var a = set.Get(Guid.NewGuid());
        var b = set.Get(Guid.NewGuid());

        a.TagNames.ShouldBeEmpty();
        a.Tags.ShouldBeEmpty();
        ReferenceEquals(a.TagNames, b.TagNames).ShouldBeTrue();
        ReferenceEquals(a.Tags, b.Tags).ShouldBeTrue();
    }

    [Fact]
    public void Get_TaggedRow_IsSortedOrdinalAndStableAcrossCalls()
    {
        var set = new DatasetCatalogStore.OverlaySet();
        var id = Guid.NewGuid();
        set.AddTag(id, new DatasetTag("b", DatasetTagSource.Admin));
        set.AddTag(id, new DatasetTag("a", DatasetTagSource.Dbt));

        var first = set.Get(id);

        first.TagNames.ShouldBe(["a", "b"]);
        ReferenceEquals(first.TagNames, set.Get(id).TagNames).ShouldBeTrue();
    }

    [Fact]
    public void Apply_SetsDiscoveryHidden_And_HiddenColumns()
    {
        var set = new DatasetCatalogStore.OverlaySet();
        var id = Guid.NewGuid();
        set.SetDiscoveryFlags(id, discoveryHidden: true, samplesDisabled: true);
        set.AddHiddenColumn(id, "secret_col");

        var meta = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            Table = new Table { Id = id, SourceName = "sales", SchemaName = "dbo", TableName = "orders" },
            Columns =
            [
                new TableColumn { TableId = id, ColumnName = "public_col" },
                new TableColumn { TableId = id, ColumnName = "secret_col" }
            ]
        };

        var applied = DatasetCatalogStore.Apply(meta, set);

        applied.Table.DiscoveryHidden.ShouldBeTrue();
        applied.Table.SamplesDisabled.ShouldBeTrue();
        applied.Columns.Single(c => c.ColumnName == "public_col").DiscoveryHidden.ShouldBeFalse();
        applied.Columns.Single(c => c.ColumnName == "secret_col").DiscoveryHidden.ShouldBeTrue();
    }

    [Fact]
    public void Degraded_Overlay_TreatsTableAsHidden()
    {
        var set = new DatasetCatalogStore.OverlaySet { AllDegraded = true };
        var id = Guid.NewGuid();

        var meta = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            Table = new Table { Id = id, SourceName = "sales", SchemaName = "dbo", TableName = "orders" },
            Columns = [new TableColumn { TableId = id, ColumnName = "col" }]
        };

        var applied = DatasetCatalogStore.Apply(meta, set);
        applied.Table.DiscoveryHidden.ShouldBeTrue();
    }
}
