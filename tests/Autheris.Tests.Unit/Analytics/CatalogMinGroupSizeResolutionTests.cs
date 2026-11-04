namespace Autheris.Tests.Unit.Analytics;

using System;
using System.Collections.Generic;
using Autheris.Application.Analytics;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogMinGroupSizeResolutionTests
{
    private static readonly TableIdentifier TestTableId = new("sales", "public", "test_table");

    private static TableMetadata CreateTableMetadata(
        int? minGroupSize = null,
        bool? nonPersonal = null,
        bool tableNonPersonal = false,
        string sensitivity = "NORMAL",
        IReadOnlyList<string>? tags = null,
        bool hasMaskingRule = false,
        bool hasSensitiveColumn = false,
        bool emptyColumns = false)
    {
        var tableId = Guid.NewGuid();
        var columns = emptyColumns
            ? Array.Empty<TableColumn>()
            : new[]
            {
                new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int" },
                new TableColumn { TableId = tableId, ColumnName = "name", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "secret", DataType = "varchar", IsSensitive = hasSensitiveColumn }
            };

        var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
        if (hasMaskingRule)
        {
            maskingRules["name"] = new MaskingRule { RuleType = "REDACT" };
        }

        return new TableMetadata
        {
            Identifier = TestTableId,
            MinGroupSize = minGroupSize,
            NonPersonal = nonPersonal,
            ColumnMaskingRules = maskingRules,
            Table = new Table
            {
                Id = tableId,
                SourceType = "PostgreSQL",
                SourceName = "default",
                SchemaName = "public",
                TableName = "test_table",
                DataSourceType = DataSourceType.Sql,
                IsActive = true,
                MinGroupSize = minGroupSize,
                NonPersonal = tableNonPersonal,
                Sensitivity = sensitivity,
                Tags = tags ?? Array.Empty<string>()
            },
            Columns = columns
        };
    }

    private static AggregationQuery CreateQuery(int? requestOverride = null)
    {
        return new AggregationQuery(
            Table: TestTableId,
            GroupBy: ["name"],
            Aggregates: [new AggregateSelection(AggregateFunction.Count, null, "cnt")],
            MinGroupSizeOverride: requestOverride);
    }

    [Fact]
    public void Resolve_NonPersonalEligibleCatalogOne_ReturnsOneWithCatalogSource()
    {
        var meta = CreateTableMetadata(minGroupSize: 1, nonPersonal: true);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true,
            CatalogMinGroupSizeFloor = 1
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(1);
        result.Source.ShouldBe(MinGroupSizeSource.Catalog);
    }

    [Fact]
    public void Resolve_CapabilityOff_KeepsDefault()
    {
        var meta = CreateTableMetadata(minGroupSize: 1, nonPersonal: true);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = false
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(5);
        result.Source.ShouldBe(MinGroupSizeSource.Default);
    }

    [Fact]
    public void Resolve_NotNonPersonal_KeepsDefault()
    {
        var meta = CreateTableMetadata(minGroupSize: 1, nonPersonal: false);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(5);
        result.Source.ShouldBe(MinGroupSizeSource.Default);
    }

    [Fact]
    public void Resolve_SensitiveColumn_KeepsDefault()
    {
        var meta = CreateTableMetadata(minGroupSize: 1, nonPersonal: true, hasSensitiveColumn: true);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(5);
        result.Source.ShouldBe(MinGroupSizeSource.Default);
    }

    [Fact]
    public void Resolve_MaskingRuleColumn_KeepsDefault()
    {
        var meta = CreateTableMetadata(minGroupSize: 1, nonPersonal: true, hasMaskingRule: true);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(5);
        result.Source.ShouldBe(MinGroupSizeSource.Default);
    }

    [Fact]
    public void Resolve_HighlySensitiveTable_KeepsDefault()
    {
        var meta = CreateTableMetadata(minGroupSize: 1, nonPersonal: true, sensitivity: "HIGH");
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(5);
        result.Source.ShouldBe(MinGroupSizeSource.Default);
    }

    [Fact]
    public void Resolve_OperatorTableOverride_Wins()
    {
        var meta = CreateTableMetadata(minGroupSize: 1, nonPersonal: true);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true
        };

        // Operator floor is explicitly set (e.g. table override = 3)
        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(3, true),
            privacy);

        result.Value.ShouldBe(3);
        result.Source.ShouldBe(MinGroupSizeSource.Operator);
    }

    [Fact]
    public void Resolve_RequestOverrideOne_StaysDefault()
    {
        var meta = CreateTableMetadata(minGroupSize: null, nonPersonal: true);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true,
            CatalogMinGroupSizeFloor = 1
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(requestOverride: 1),
            new OperatorFloor(5, false),
            privacy);

        // Request override may only raise, not lower
        result.Value.ShouldBe(5);
        result.Source.ShouldBe(MinGroupSizeSource.Default);
    }

    [Fact]
    public void Resolve_RequestOverrideEight_RaisesWithRequestSource()
    {
        var meta = CreateTableMetadata(minGroupSize: null, nonPersonal: true);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(requestOverride: 8),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(8);
        result.Source.ShouldBe(MinGroupSizeSource.Request);
    }

    [Fact]
    public void Resolve_CatalogHigherThanDefault_RaisesFloor()
    {
        var meta = CreateTableMetadata(minGroupSize: 10, nonPersonal: false);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(10);
        result.Source.ShouldBe(MinGroupSizeSource.Catalog);
    }

    [Fact]
    public void Resolve_JoinWithIneligibleTable_UsesMaxFloor()
    {
        var eligible = CreateTableMetadata(minGroupSize: 1, nonPersonal: true);
        var ineligible = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "users"),
            MinGroupSize = null,
            NonPersonal = false,
            Table = new Table { Id = Guid.NewGuid(), Sensitivity = "NORMAL", IsActive = true, TableName = "users", SchemaName = "public", SourceName = "default", SourceType = "PostgreSQL" },
            Columns = [new TableColumn { ColumnName = "user_id" }]
        };

        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true
        };

        var result = GovernedAggregationService.Resolve(
            [eligible, ineligible],
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(5);
        result.Source.ShouldBe(MinGroupSizeSource.Default);
    }

    [Fact]
    public void Resolve_EmptyOrUnloadedColumns_DoesNotLower()
    {
        var meta = CreateTableMetadata(minGroupSize: 1, nonPersonal: true, emptyColumns: true);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(5);
        result.Source.ShouldBe(MinGroupSizeSource.Default);
    }

    [Fact]
    public void Resolve_TableLevelPiiTag_DoesNotLower()
    {
        var meta = CreateTableMetadata(minGroupSize: 1, nonPersonal: true, tags: ["pii"]);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(5);
        result.Source.ShouldBe(MinGroupSizeSource.Default);
    }

    [Fact]
    public void Resolve_DbtOnlyNonPersonal_DoesNotLowerWithoutSourceDeclaredOption()
    {
        // Governance overlay is null, only table layer (dbt) has nonPersonal = true
        var meta = CreateTableMetadata(minGroupSize: 1, nonPersonal: null, tableNonPersonal: true);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true,
            AllowSourceDeclaredLowering = false
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(5);
        result.Source.ShouldBe(MinGroupSizeSource.Default);
    }

    [Fact]
    public void Resolve_DbtOnlyNonPersonal_LowersWithSourceDeclaredOption()
    {
        var meta = CreateTableMetadata(minGroupSize: 1, nonPersonal: null, tableNonPersonal: true);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true,
            AllowSourceDeclaredLowering = true,
            CatalogMinGroupSizeFloor = 1
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(1);
        result.Source.ShouldBe(MinGroupSizeSource.Catalog);
    }

    [Fact]
    public void Resolve_OverlayNonPersonalFalse_PinsOff()
    {
        // Table has true, but governance overlay explicitly set false
        var meta = CreateTableMetadata(minGroupSize: 1, nonPersonal: false, tableNonPersonal: true);
        var privacy = new AnalyticsPrivacyOptions
        {
            DefaultMinGroupSize = 5,
            AllowCatalogLowering = true,
            AllowSourceDeclaredLowering = true
        };

        var result = GovernedAggregationService.Resolve(
            meta,
            CreateQuery(),
            new OperatorFloor(5, false),
            privacy);

        result.Value.ShouldBe(5);
        result.Source.ShouldBe(MinGroupSizeSource.Default);
    }

    [Fact]
    public void NoisyCacheKey_SameValueDifferentSource_DoesNotShare()
    {
        var keyCatalog = NoisyCacheKey.Build(
            new TenantId("tenant-a"),
            new Sid("alice"),
            TestTableId,
            ["status"],
            [new AggregateSelection(AggregateFunction.Count, null, "cnt")],
            null,
            null,
            10,
            5,
            SmallGroupStrategy.Suppress,
            "20261018",
            "fp",
            null,
            minGroupSizeSource: MinGroupSizeSource.Catalog);

        var keyOperator = NoisyCacheKey.Build(
            new TenantId("tenant-a"),
            new Sid("alice"),
            TestTableId,
            ["status"],
            [new AggregateSelection(AggregateFunction.Count, null, "cnt")],
            null,
            null,
            10,
            5,
            SmallGroupStrategy.Suppress,
            "20261018",
            "fp",
            null,
            minGroupSizeSource: MinGroupSizeSource.Operator);

        keyCatalog.ShouldNotBe(keyOperator);
    }
}

