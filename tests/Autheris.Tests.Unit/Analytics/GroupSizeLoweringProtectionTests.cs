using Autheris.Application.Analytics;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Analytics;

[Trait("Category", "Security")]
public sealed class GroupSizeLoweringProtectionTests
{
    private static readonly TableIdentifier TableId = new("analytics", "public", "events");

    private static TableMetadata Metadata(string[]? tags = null) => new()
    {
        Identifier = TableId,
        MinGroupSize = 1,
        NonPersonal = true,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "analytics",
            SchemaName = "public",
            TableName = "events",
            IsActive = true,
            MinGroupSize = 1,
            NonPersonal = true,
            Tags = tags ?? []
        },
        Columns =
        [
            new TableColumn { ColumnName = "event_type", DataType = "varchar" },
            new TableColumn { ColumnName = "account_ref", DataType = "varchar" }
        ]
    };

    private static EffectiveProtection Protection(
        ProtectionClass tableClass,
        string? tableMaskCategory = null,
        IReadOnlyDictionary<string, ColumnProtection>? columns = null) =>
        new(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            null,
            tableClass,
            tableClass,
            tableMaskCategory,
            columns ?? new Dictionary<string, ColumnProtection>(),
            CatalogVisibilityMode.Open);

    private static AggregationQuery Query() =>
        new(TableId, ["event_type"], [new AggregateSelection(AggregateFunction.Count, null, "cnt")]);

    [Theory]
    [InlineData(ProtectionClass.Public, true)]
    [InlineData(ProtectionClass.Internal, true)]
    [InlineData(ProtectionClass.Confidential, false)]
    [InlineData(ProtectionClass.Secret, false)]
    public void Only_public_or_internal_unmasked_tables_lower_group_size(ProtectionClass tableClass, bool shouldLower)
    {
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true,
            CatalogMinGroupSizeFloor = 1
        };

        var result = GovernedAggregationService.Resolve(
            Metadata(),
            Query(),
            new OperatorFloor(5, false),
            privacy,
            protection: Protection(tableClass));

        result.Value.ShouldBe(shouldLower ? 1 : 5);
    }

    [Fact]
    public void Table_or_column_mask_category_blocks_lowering_and_tags_carry_no_permission()
    {
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true,
            CatalogMinGroupSizeFloor = 1
        };

        // Tags on table do not block lowering when protection class is Public and unmasked.
        var taggedPublic = GovernedAggregationService.Resolve(
            Metadata(tags: ["pii", "restricted"]),
            Query(),
            new OperatorFloor(5, false),
            privacy,
            protection: Protection(ProtectionClass.Public));
        taggedPublic.Value.ShouldBe(1);

        // Table mask category blocks lowering even if table class is Public.
        var maskedTable = GovernedAggregationService.Resolve(
            Metadata(),
            Query(),
            new OperatorFloor(5, false),
            privacy,
            protection: Protection(ProtectionClass.Public, tableMaskCategory: "FINANCIAL"));
        maskedTable.Value.ShouldBe(5);

        // Column mask category blocks lowering.
        var maskedCol = GovernedAggregationService.Resolve(
            Metadata(),
            Query(),
            new OperatorFloor(5, false),
            privacy,
            protection: Protection(
                ProtectionClass.Internal,
                columns: new Dictionary<string, ColumnProtection>
                {
                    ["account_ref"] = new(ProtectionClass.Internal, "PII")
                }));
        maskedCol.Value.ShouldBe(5);
    }
}
