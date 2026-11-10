namespace Autheris.Tests.Unit.Catalog;

using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class DiscoveryDomainModelTests
{
    [Fact]
    public void Table_And_TableColumn_HaveDiscoveryProperties()
    {
        var col = new TableColumn
        {
            ColumnName = "secret_col",
            DiscoveryHidden = true
        };
        col.DiscoveryHidden.ShouldBeTrue();

        var table = new Table
        {
            DiscoveryHidden = true,
            SamplesDisabled = true,
            ObjectKind = DiscoveryObjectKind.View
        };
        table.DiscoveryHidden.ShouldBeTrue();
        table.SamplesDisabled.ShouldBeTrue();
        table.ObjectKind.ShouldBe(DiscoveryObjectKind.View);

        var defaultTable = new Table();
        defaultTable.DiscoveryHidden.ShouldBeFalse();
        defaultTable.SamplesDisabled.ShouldBeFalse();
        defaultTable.ObjectKind.ShouldBe(DiscoveryObjectKind.Table);
    }

    [Fact]
    public void DiscoveryObject_And_RelatedRecords_ConstructCorrectly()
    {
        var colStats = new DiscoveryColumnStatistics(
            Nullable: false,
            NullRatio: 0.0,
            ApproxDistinctCount: 100,
            MinLength: 1,
            MaxLength: 10,
            MinValue: "A",
            MaxValue: "Z",
            TopValues: ["A", "B", "C"]);

        var col = new DiscoveryColumn(
            Name: "col1",
            DisplayName: "Column 1",
            DataType: "string",
            Description: "A column",
            Pii: true,
            ClearAccessRequiresSeparateApproval: true,
            State: DiscoveryColumnState.Masked,
            Statistics: colStats,
            ClearInSomeRows: true);

        col.Name.ShouldBe("col1");
        col.ClearInSomeRows.ShouldBeTrue();

        var tableStats = new DiscoveryTableStatistics(
            ApproxRowCount: 1000,
            SizeBytes: 10240,
            LastUpdatedAt: DateTimeOffset.UtcNow,
            PartitionColumns: ["date"],
            UploadedAt: DateTimeOffset.UtcNow);

        var owner = new DiscoveryOwner("Owner", "owner@example.com");

        var obj = new DiscoveryObject(
            Kind: DiscoveryObjectKind.Table,
            Domain: "sales",
            Schema: "dbo",
            Name: "orders",
            DisplayName: "Orders",
            Description: "Orders table",
            Sensitivity: "NORMAL",
            Tags: ["pii"],
            Owners: [owner],
            Access: DiscoveryAccessState.Granted,
            RowScope: DiscoveryRowScope.Full,
            Columns: [col],
            Statistics: tableStats,
            SamplesAvailable: true,
            IsHidden: false);

        obj.Kind.ShouldBe(DiscoveryObjectKind.Table);
        obj.Access.ShouldBe(DiscoveryAccessState.Granted);
        obj.RowScope.ShouldBe(DiscoveryRowScope.Full);
        obj.Columns.Count.ShouldBe(1);
        obj.Owners.Single().DisplayName.ShouldBe("Owner");
    }
}
