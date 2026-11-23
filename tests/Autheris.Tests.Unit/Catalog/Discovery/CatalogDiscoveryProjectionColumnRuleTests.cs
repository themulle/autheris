namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Catalog.Discovery;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>One rule for the metadata of a denied column: it is never listed, described, counted or named in keys, filters or statistics.</summary>
[Trait("Category", "Security")]
public sealed class CatalogDiscoveryProjectionColumnRuleTests
{
    private const string Denied = "diagnosis_code";

    private static TableMetadata Table()
    {
        var t = DiscoveryTestBuilder.MakeTable(
            "crm", "patients", columns: [("id", false, false), ("fullname", false, false), (Denied, false, false)]);
        return t with
        {
            Columns = t.Columns.Select(c => new TableColumn { ColumnName = c.ColumnName, DataType = c.DataType, Description = c.ColumnName == Denied ? "zebracornmarker" : null }).ToList(),
            PrimaryKeyColumns = ["id", Denied],
            PrimaryKeyDirections = [KeyColumnDirection.Ascending, KeyColumnDirection.Ascending],
            Indexes =
            [
                new TableIndexDefinition("ix_id", ["id"], false),
                new TableIndexDefinition("ix_composite", ["id", Denied], false)
            ],
            Table = t.Table with { RequiredFilterColumns = [Denied], RequiredFilterColumnGroups = [[Denied, "id"]] }
        };
    }

    private static DiscoveryObject Map(bool granted, bool withStatistics = false)
    {
        var table = Table();
        var decision = granted
            ? TableAccessDecision.Allowed(
                table.Identifier,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { [Denied] = ColumnAccessLevel.Deny },
                hasUnconstrainedColumnAllow: true)
            : TableAccessDecision.Denied(table.Identifier, "no consent");
        DiscoveryArtifacts? artifacts = withStatistics
            ? new DiscoveryArtifacts(
                table.Table.Id, "tenant-a", null,
                $"{{\"table\":{{\"approxRowCount\":42,\"partitionColumns\":[\"id\",\"{Denied}\"]}}}}",
                "abc", "S-ADMIN", DateTimeOffset.UtcNow)
            : null;
        return DiscoveryAccessMapper.Map(table, decision, [], false, false, artifacts, language: null, isHidden: false);
    }

    [Fact]
    public void ToDatasetDetail_GrantedObject_DeniedColumn_IsNotListed()
    {
        var detail = CatalogDiscoveryProjection.ToDatasetDetail(Map(granted: true), null);

        detail.Columns.Select(c => c.Name).ShouldBe(["id", "fullname"]);
        detail.Columns.ShouldAllBe(c => c.MaskingState == "Clear" || c.MaskingState == "Masked");
    }

    [Fact]
    public void ToDatasetDetail_UngrantedObject_ListsNoColumn()
    {
        var detail = CatalogDiscoveryProjection.ToDatasetDetail(Map(granted: false), null);

        detail.Columns.ShouldBeEmpty();
        detail.Table.ShouldBe("patients");
    }

    [Fact]
    public void ToDatasetDetail_UnknownColumnState_IsNotListed()
    {
        var obj = Map(granted: true);
        obj = obj with { Columns = obj.Columns.Select(c => c with { State = (DiscoveryColumnState)99 }).ToList() };

        CatalogDiscoveryProjection.ToDatasetDetail(obj, null).Columns.ShouldBeEmpty();
    }

    [Fact]
    public void ToDatasetDetail_NeverEmitsDenyMaskingState()
    {
        foreach (var granted in new[] { true, false })
        {
            var detail = CatalogDiscoveryProjection.ToDatasetDetail(Map(granted), null);
            detail.Columns.ShouldNotContain(c => string.Equals(c.MaskingState, "Deny", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void MapQueryShape_DeniedColumnInCompositeIndex_OmitsIndexWhole()
    {
        var shape = Map(granted: true).QueryShape!;

        shape.Indexes.ShouldHaveSingleItem().Columns.ShouldBe(["id"]);
        shape.PrimaryKeyColumns.ShouldNotContain(Denied);
        shape.RequiredFilterColumns.ShouldNotContain(Denied);
        shape.RequiredFilterColumnGroups.SelectMany(g => g).ShouldNotContain(Denied);
    }

    [Fact]
    public void MapQueryShape_UngrantedObject_NamesNoColumn()
    {
        var shape = Map(granted: false).QueryShape ?? DiscoveryQueryShape.Empty;

        shape.PrimaryKeyColumns.ShouldBeEmpty();
        shape.Indexes.ShouldBeEmpty();
        shape.RequiredFilterColumns.ShouldBeEmpty();
        shape.RequiredFilterColumnGroups.ShouldBeEmpty();
    }

    [Fact]
    public void Statistics_PartitionColumn_Denied_IsNotNamed_Granted()
    {
        Map(granted: true, withStatistics: true).Statistics!.PartitionColumns.ShouldBe(["id"]);
    }

    [Fact]
    public void Statistics_PartitionColumn_Denied_IsNotNamed_Ungranted()
    {
        Map(granted: false, withStatistics: true).Statistics.ShouldBeNull();
    }
}
