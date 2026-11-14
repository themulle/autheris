namespace Autheris.Tests.Unit.Catalog;

using System.Collections.Generic;
using Autheris.Application.Catalog;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static CatalogProjectionTestData;

public sealed class CatalogTableProjectionTests
{
    private static CatalogProjectionDiff Diff(TableMetadata before, TableMetadata after) =>
        CatalogTablePersistedProjection.Compare(
            CatalogTablePersistedProjection.From(before),
            CatalogTablePersistedProjection.From(after));

    [Fact]
    public void EqualMetadata_IsUnchanged()
    {
        var diff = Diff(Meta(masks: Mask("name")), Meta(masks: Mask("name")));

        diff.Classification.ShouldBe(CatalogWriteClassification.Unchanged);
        diff.ChangedPathCount.ShouldBe(0);
    }

    [Fact]
    public void EqualMetadata_WithDifferentSurrogateIds_IsUnchanged()
    {
        var before = Meta();
        var after = before with { Table = before.Table with { Id = System.Guid.NewGuid() } };

        Diff(before, after).Classification.ShouldBe(CatalogWriteClassification.Unchanged);
    }

    [Fact]
    public void ColumnTypeChange_IsChanged()
    {
        var before = Meta(columns: [Column("id", "int"), Column("name", "varchar")]);
        var after = Meta(columns: [Column("id", "int"), Column("name", "text")]);

        Diff(before, after).Classification.ShouldBe(CatalogWriteClassification.Restrictive);
    }

    [Fact]
    public void DescriptionChange_IsChanged()
    {
        var before = Meta();
        var after = before with { Table = before.Table with { Description = "Changed text" } };

        var diff = Diff(before, after);

        diff.Classification.ShouldBe(CatalogWriteClassification.Additive);
        diff.ChangedPathCount.ShouldBe(1);
    }

    [Fact]
    public void OrderOfColumnsIrrelevantForComparison()
    {
        var before = Meta(columns: [Column("id", "int"), Column("name"), Column("email")]);
        var after = Meta(columns: [Column("email"), Column("id", "int"), Column("name")]);

        Diff(before, after).Classification.ShouldBe(CatalogWriteClassification.Unchanged);
    }

    [Fact]
    public void NewColumn_IsAdditive()
    {
        var before = Meta(columns: [Column("id", "int")]);
        var after = Meta(columns: [Column("id", "int"), Column("name", isSensitive: true)]);

        Diff(before, after).Classification.ShouldBe(CatalogWriteClassification.Additive);
    }

    [Fact]
    public void NewTable_IsAdditive()
    {
        CatalogTablePersistedProjection.Compare(null, CatalogTablePersistedProjection.From(Meta()))
            .Classification.ShouldBe(CatalogWriteClassification.Additive);
    }

    [Fact]
    public void RemovedColumn_IsRestrictive()
    {
        var before = Meta(columns: [Column("id", "int"), Column("name")]);
        var after = Meta(columns: [Column("id", "int")]);

        Diff(before, after).Classification.ShouldBe(CatalogWriteClassification.Restrictive);
    }

    [Fact]
    public void MetaAndKeyChanges_AreRestrictive()
    {
        var before = Meta();
        var withMeta = Meta(columns: [Column("id", "int"), Column("name", meta: new Dictionary<string, string> { ["role"] = "pii" })]);
        var withKey = Meta(primaryKey: ["name"]);

        Diff(before, withMeta).Classification.ShouldBe(CatalogWriteClassification.Restrictive);
        Diff(before, withKey).Classification.ShouldBe(CatalogWriteClassification.Restrictive);
    }

    [Fact]
    public void ColumnNameContainingSeparators_DoesNotCollide()
    {
        var before = Meta(columns: [Column("a].data_type"), Column("a")]);
        var after = Meta(columns: [Column("a].data_type", "int"), Column("a")]);

        Diff(before, after).Classification.ShouldBe(CatalogWriteClassification.Restrictive);
    }
}
