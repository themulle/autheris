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
        var after = Meta(columns: [Column("id", "int"), Column("name")]) with { ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("name") };

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

    [Fact]
    public void GlobalColumnClassificationsAndOverrides_AreProjectedAndClassifiedAsRestrictive()
    {
        var fixedTime = new System.DateTimeOffset(2026, 11, 15, 0, 0, 0, System.TimeSpan.Zero);
        var baseline = Meta();

        var withClassification = baseline with
        {
            ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>
            {
                ["name"] =
                [
                    new ColumnClassification
                    {
                        Scope = "*",
                        TableColumnId = "col-name",
                        Code = "PII",
                        Source = "INGESTION",
                        AssignedBy = "dbt",
                        AssignedAt = fixedTime
                    }
                ]
            }
        };

        Diff(baseline, withClassification).Classification.ShouldBe(CatalogWriteClassification.Restrictive);
        Diff(withClassification, withClassification).Classification.ShouldBe(CatalogWriteClassification.Unchanged);

        var withOverride = withClassification with
        {
            ColumnMaskOverrides = new Dictionary<string, IReadOnlyList<ColumnMaskOverride>>
            {
                ["name"] =
                [
                    new ColumnMaskOverride
                    {
                        Id = System.Guid.NewGuid(),
                        Scope = "*",
                        TableColumnId = "col-name",
                        Code = "PII",
                        MaskFunction = MaskFunction.Hash,
                        KeyRef = "kv-key",
                        CatalogText = CatalogTextVisibility.Visible,
                        UpdatedBy = "owner",
                        UpdatedAt = fixedTime
                    }
                ]
            }
        };

        Diff(withClassification, withOverride).Classification.ShouldBe(CatalogWriteClassification.Restrictive);

        // Surrogate Id difference or tenant-scoped override does not change global projection.
        var reorderedEquivalent = withOverride with
        {
            ColumnMaskOverrides = new Dictionary<string, IReadOnlyList<ColumnMaskOverride>>
            {
                ["name"] =
                [
                    withOverride.ColumnMaskOverrides["name"][0] with { Id = System.Guid.NewGuid() },
                    new ColumnMaskOverride
                    {
                        Id = System.Guid.NewGuid(),
                        Scope = "tenant-a",
                        TableColumnId = "col-name",
                        Code = "PII",
                        MaskFunction = MaskFunction.Nullify,
                        CatalogText = CatalogTextVisibility.Hidden,
                        UpdatedBy = "tenant-admin",
                        UpdatedAt = fixedTime
                    }
                ]
            }
        };

        Diff(withOverride, reorderedEquivalent).Classification.ShouldBe(CatalogWriteClassification.Unchanged);

        // Switching catalog_text to Hidden on global override is restrictive.
        var hiddenOverride = withOverride with
        {
            ColumnMaskOverrides = new Dictionary<string, IReadOnlyList<ColumnMaskOverride>>
            {
                ["name"] =
                [
                    withOverride.ColumnMaskOverrides["name"][0] with { CatalogText = CatalogTextVisibility.Hidden }
                ]
            }
        };

        Diff(withOverride, hiddenOverride).Classification.ShouldBe(CatalogWriteClassification.Restrictive);
    }
}
