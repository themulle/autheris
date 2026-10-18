namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

/// <summary>Issue #111 T-R2-4 (defaults), A-9 audit policy and A-9 configuration defaults.</summary>
public sealed class CatalogViewDefaultsTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void T_R2_4_NewTableMetadata_IsUnmarkedWithNoCatalogTags()
    {
        var meta = new TableMetadata();

        meta.CatalogView.ShouldBe(CatalogView.Unmarked);
        ((int)CatalogView.Unmarked).ShouldBe(0);
        meta.CatalogTags.ShouldBeEmpty();
    }

    [Fact]
    public void CatalogAuditEvents_AreFailClosed()
    {
        AuditPolicyTable.For(AuditEventTypes.CatalogVisibilityChanged).ShouldBe(AuditFailurePolicy.FailClosed);
        AuditPolicyTable.For(AuditEventTypes.CatalogTagsChanged).ShouldBe(AuditFailurePolicy.FailClosed);
        AuditPolicyTable.All.ShouldContainKey(AuditEventTypes.CatalogVisibilityChanged);
        AuditPolicyTable.All.ShouldContainKey(AuditEventTypes.CatalogTagsChanged);
    }

    [Fact]
    public void DataCatalogOptions_Defaults()
    {
        var o = new DataCatalogOptions();
        o.MaxTagsPerDataset.ShouldBe(32);
        o.DefaultVisibility.ShouldBe(DatasetVisibility.Hidden);
    }

    [Fact]
    public void DatasetCatalogChange_Changed_ComparesTagSetsAndVisibility()
    {
        var id = new TableIdentifier("d", "s", "t");
        var a = new DatasetCatalogSettings([new DatasetTag("x", DatasetTagSource.Admin)], DatasetVisibility.Hidden);
        new DatasetCatalogChange(id, a, a with { Tags = [new DatasetTag("x", DatasetTagSource.Admin)] }).Changed.ShouldBeFalse();
        new DatasetCatalogChange(id, a, a with { Visibility = DatasetVisibility.Summary }).Changed.ShouldBeTrue();
        new DatasetCatalogChange(id, a, a with { Tags = [new DatasetTag("x", DatasetTagSource.Dbt)] }).Changed.ShouldBeTrue();
        new DatasetCatalogChange(id, a, a with { Tags = [] }).Changed.ShouldBeTrue();
    }
}
