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
}
