namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Catalog;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static CatalogProjectionTestData;

/// <summary>#674 (SEC-OI-674-2, -4): a false "unchanged" would silently drop a restrictive catalog change.</summary>
[Trait("Category", "Security")]
public sealed class CatalogNoOpSecurityTests
{
    private static CatalogWriteClassification Classify(TableMetadata before, TableMetadata after) =>
        CatalogTablePersistedProjection.Compare(
            CatalogTablePersistedProjection.From(before),
            CatalogTablePersistedProjection.From(after)).Classification;

    [Fact]
    public void MaskingRuleChangeOnly_IsNeverTreatedAsUnchanged()
    {
        var plain = Meta();
        var masked = Meta(masks: Mask("name"));
        var otherRule = Meta(masks: Mask("name", "HMAC_SHA256"));
        var otherReplacement = Meta(masks: Mask("name", "REDACT", "***"));

        Classify(plain, masked).ShouldBe(CatalogWriteClassification.Restrictive);
        Classify(masked, plain).ShouldBe(CatalogWriteClassification.Restrictive);
        Classify(masked, otherRule).ShouldBe(CatalogWriteClassification.Restrictive);
        Classify(masked, otherReplacement).ShouldBe(CatalogWriteClassification.Restrictive);
    }

    [Fact]
    public void CatalogTextHiddenChange_IsRestrictive_AndFlushesImmediately()
    {
        // A visible-to-hidden switch of the catalog text of a column override is a restrictive change, so the writer bumps the table epoch
        // and flushes the catalog version at once (an additive or unchanged classification would be batched).
        ColumnMaskOverride Override(CatalogTextVisibility text) => new()
        {
            TableColumnId = "col-name",
            Code = "PII_SPECIAL",
            CatalogText = text,
            UpdatedBy = "admin"
        };

        TableMetadata With(CatalogTextVisibility text) => Meta() with
        {
            ColumnMaskOverrides = new Dictionary<string, IReadOnlyList<ColumnMaskOverride>>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = [Override(text)]
            }
        };

        Classify(With(CatalogTextVisibility.Visible), With(CatalogTextVisibility.Hidden)).ShouldBe(CatalogWriteClassification.Restrictive);
        Classify(With(CatalogTextVisibility.Hidden), With(CatalogTextVisibility.Visible)).ShouldBe(CatalogWriteClassification.Restrictive);
        Classify(With(CatalogTextVisibility.Hidden), With(CatalogTextVisibility.Hidden)).ShouldBe(CatalogWriteClassification.Unchanged);
    }

    [Fact]
    public void ClassificationChangeOnly_BumpsEpoch()
    {
        var before = Meta();
        var after = before with { ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("name") };
        var nonPersonal = before with { Table = before.Table with { NonPersonal = true } };

        Classify(before, after).ShouldBe(CatalogWriteClassification.Restrictive);
        Classify(before, nonPersonal).ShouldBe(CatalogWriteClassification.Restrictive);
    }

    [Fact]
    public void IsActiveChangeOnly_BumpsEpoch()
    {
        var active = Meta();
        var inactive = active with { Table = active.Table with { IsActive = false } };

        Classify(active, inactive).ShouldBe(CatalogWriteClassification.Restrictive);
        Classify(inactive, active).ShouldBe(CatalogWriteClassification.Restrictive);
    }

    [Fact]
    public void NullVersusEmpty_IsChanged()
    {
        var withNull = Meta(columns: [Column("id", "int"), Column("name", description: null)]);
        var withEmpty = Meta(columns: [Column("id", "int"), Column("name", description: string.Empty)]);
        var withSpace = Meta(columns: [Column("id", "int"), Column("name", description: " ")]);

        Classify(withNull, withEmpty).ShouldNotBe(CatalogWriteClassification.Unchanged);
        Classify(withEmpty, withSpace).ShouldNotBe(CatalogWriteClassification.Unchanged);
    }

    [Fact]
    public void CaseOnlyIdentifierChange_IsChanged()
    {
        var before = Meta();
        var tableCase = before with { Table = before.Table with { TableName = "Customers" } };
        var columnCase = Meta(columns: [Column("id", "int"), Column("Name")]);
        var typeCase = Meta(columns: [Column("id", "INT"), Column("name")]);

        Classify(before, tableCase).ShouldBe(CatalogWriteClassification.Restrictive);
        Classify(before, columnCase).ShouldNotBe(CatalogWriteClassification.Unchanged);
        Classify(before, typeCase).ShouldBe(CatalogWriteClassification.Restrictive);
    }

    [Fact]
    public void ComparerException_FallsBackToWrite()
    {
        // An ambiguous state cannot be projected; the repository then writes instead of treating the table as unchanged.
        var duplicateColumns = Meta(columns: [Column("id", "int"), Column("id", "int")]);
        var orphanMask = Meta(masks: Mask("missing"));

        Should.Throw<InvalidOperationException>(() => CatalogTablePersistedProjection.From(duplicateColumns));
        Should.Throw<InvalidOperationException>(() => CatalogTablePersistedProjection.From(orphanMask));
    }

    [Fact]
    public void EveryProjectedProperty_FlipIsChanged()
    {
        var baseline = Meta(
            columns: [Column("id", "int"), Column("name", description: "n", collation: "C")],
            masks: Mask("name"),
            indexes: [new TableIndexDefinition("ix_name", ["name"], false)]);

        var flips = new Dictionary<string, TableMetadata>
        {
            ["Table.SourceType"] = baseline with { Table = baseline.Table with { SourceType = "SqlServer" } },
            ["Table.SourceName"] = baseline with { Table = baseline.Table with { SourceName = "other" } },
            ["Table.SchemaName"] = baseline with { Table = baseline.Table with { SchemaName = "other" } },
            ["Table.TableName"] = baseline with { Table = baseline.Table with { TableName = "other" } },
            ["Table.DisplayName"] = baseline with { Table = baseline.Table with { DisplayName = "Other" } },
            ["Table.Description"] = baseline with { Table = baseline.Table with { Description = "Other" } },
            ["Table.LongDescription"] = baseline with { Table = baseline.Table with { LongDescription = "Other" } },
            ["Table.DocumentationSource"] = baseline with { Table = baseline.Table with { DocumentationSource = "dbt" } },
            ["Table.IsActive"] = baseline with { Table = baseline.Table with { IsActive = false } },
            ["Table.DataSourceType"] = baseline with { Table = baseline.Table with { DataSourceType = DataSourceType.HttpDeclarative } },
            ["Table.HttpEndpoint"] = baseline with { Table = baseline.Table with { HttpEndpoint = new HttpEndpointDescriptor { Id = Guid.Empty, Name = "ep", BaseUrl = "https://api.example.test", PathTemplate = "/v1" } } },
            ["Table.PluginName"] = baseline with { Table = baseline.Table with { PluginName = "plugin" } },
            ["Table.MinGroupSize"] = baseline with { Table = baseline.Table with { MinGroupSize = 5 } },
            ["Table.NonPersonal"] = baseline with { Table = baseline.Table with { NonPersonal = true } },
            ["Table.RequiredFilterColumns"] = baseline with { Table = baseline.Table with { RequiredFilterColumns = Filters("id") } },
            ["Table.Texts"] = baseline with
            {
                Table = baseline.Table with
                {
                    Texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry> { ["de"] = new("Kunden") })
                }
            },
            ["Column.ColumnName"] = baseline with { Columns = [Column("row_id", "int"), baseline.Columns[1]] },
            ["Column.DisplayName"] = baseline with
            {
                Columns =
                [
                    baseline.Columns[0],
                    new TableColumn { ColumnName = "name", DisplayName = "Full Name", DataType = "varchar", Description = "n", Collation = "C" }
                ]
            },
            ["Column.DataType"] = baseline with { Columns = [Column("id", "bigint"), baseline.Columns[1]] },
            ["Column.Classification"] = baseline with { Columns = [baseline.Columns[0], Column("name", description: "n", collation: "C")], ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("name") },
            ["Column.Description"] = baseline with { Columns = [baseline.Columns[0], Column("name", description: "x", collation: "C")] },
            ["Column.LongDescription"] = baseline with
            {
                Columns =
                [
                    baseline.Columns[0],
                    new TableColumn { ColumnName = "name", DataType = "varchar", Description = "n", LongDescription = "Long", Collation = "C" }
                ]
            },
            ["Column.Texts"] = baseline with
            {
                Columns =
                [
                    baseline.Columns[0],
                    new TableColumn
                    {
                        ColumnName = "name",
                        DataType = "varchar",
                        Description = "n",
                        Collation = "C",
                        Texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry> { ["de"] = new("Name") })
                    }
                ]
            },
            ["Column.DocumentationSource"] = baseline with
            {
                Columns =
                [
                    baseline.Columns[0],
                    new TableColumn { ColumnName = "name", DataType = "varchar", Description = "n", Collation = "C", DocumentationSource = "dbt" }
                ]
            },
            ["Column.Collation"] = baseline with { Columns = [baseline.Columns[0], Column("name", description: "n", collation: "other")] },
            ["Column.LengthSemantics"] = baseline with
            {
                Columns =
                [
                    baseline.Columns[0],
                    new TableColumn { ColumnName = "name", DataType = "varchar", Description = "n", Collation = "C", LengthSemantics = "CHAR" }
                ]
            },
            ["Column.Meta"] = baseline with
            {
                Columns = [baseline.Columns[0], Column("name", description: "n", collation: "C", meta: new Dictionary<string, string> { ["k"] = "v" })]
            },
            ["Mask.RuleType"] = baseline with { ColumnMaskingRules = Mask("name", "NULLIFY") },
            ["Mask.PatternOrFormat"] = baseline with
            {
                ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.Ordinal)
                {
                    ["name"] = new MaskingRule { RuleType = "REDACT", PatternOrFormat = "###" }
                }
            },
            ["Mask.Replacement"] = baseline with { ColumnMaskingRules = Mask("name", "REDACT", "x") },
            ["Mask.HmacKeyId"] = baseline with
            {
                ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.Ordinal)
                {
                    ["name"] = new MaskingRule { RuleType = "REDACT", HmacKeyId = "key-1" }
                }
            },
            ["Key.Primary"] = baseline with { PrimaryKeyColumns = ["id", "name"] },
            ["Key.PrimaryDirections"] = baseline with { PrimaryKeyDirections = [KeyColumnDirection.Descending] },
            ["Key.Index"] = baseline with { Indexes = [new TableIndexDefinition("ix_name", ["name"], true)] }
        };

        foreach (var (name, flipped) in flips)
        {
            Classify(baseline, flipped).ShouldNotBe(CatalogWriteClassification.Unchanged, $"flipping {name} must be detected");
        }

        // Only documentation texts are additive; every other flip is restrictive.
        var additive = new HashSet<string>
        {
            "Table.DisplayName", "Table.Description", "Table.LongDescription", "Table.DocumentationSource", "Table.Texts",
            "Column.DisplayName", "Column.Description", "Column.LongDescription", "Column.DocumentationSource", "Column.Texts"
        };
        foreach (var (name, flipped) in flips.Where(f => !additive.Contains(f.Key)))
        {
            Classify(baseline, flipped).ShouldBe(CatalogWriteClassification.Restrictive, $"flipping {name} must not be deferrable");
        }
    }
}
