using System.Collections.Immutable;
using System.Reflection;
using Autheris.Application.DataCatalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Catalog;

/// <summary>Issue #94 (SEC-94-11): the ratchet carries an incoming key definition only when it asks to replace it.</summary>
public sealed class CatalogGovernanceRatchetTests
{
    private static TableMetadata Meta(IReadOnlyList<string> pk, IReadOnlyList<TableIndexDefinition> indexes, bool replace) => new()
    {
        Identifier = new TableIdentifier("d", "s", "t"),
        Table = new Table { SourceName = "d", SchemaName = "s", TableName = "t", DisplayName = "t" },
        Columns = [new TableColumn { ColumnName = "a" }, new TableColumn { ColumnName = "b" }],
        PrimaryKeyColumns = pk,
        Indexes = indexes,
        ReplaceKeyDefinition = replace
    };

    [Fact]
    public void Merge_CarriesIncomingDefinitionOnlyWhenFlagged()
    {
        var existing = Meta(["a"], [new TableIndexDefinition("ix_a", ["a"], false)], replace: false);
        var incoming = Meta(["b"], [new TableIndexDefinition("ux_b", ["b"], true)], replace: true);

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        merged.ReplaceKeyDefinition.ShouldBeTrue();
        merged.PrimaryKeyColumns.ShouldBe(["b"]);
        merged.Indexes.Single().Name.ShouldBe("ux_b");
    }

    [Fact]
    public void Merge_UnflaggedIncomingDefinition_KeepsExistingDefinition_AndStaysUnflagged()
    {
        var existing = Meta(["a"], [new TableIndexDefinition("ix_a", ["a"], false)], replace: false);
        var incoming = Meta(["b"], [new TableIndexDefinition("ux_b", ["b"], true)], replace: false);

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        merged.ReplaceKeyDefinition.ShouldBeFalse();
        merged.PrimaryKeyColumns.ShouldBe(["a"]);
        merged.Indexes.Single().Name.ShouldBe("ix_a");
    }

    [Fact]
    public void Merge_FlaggedEmptyIncoming_Clears()
    {
        var existing = Meta(["a"], [], replace: false);
        var merged = CatalogGovernanceRatchet.Merge(Meta([], [], replace: true), existing);
        merged.PrimaryKeyColumns.ShouldBeEmpty();
        merged.ReplaceKeyDefinition.ShouldBeTrue();
    }

    // Table properties a catalog snapshot owns (descriptive fields and ratcheted governance flags). Every other settable
    // property must survive a merge unchanged.
    private static readonly HashSet<string> CatalogOwnedTableProperties = new(StringComparer.Ordinal)
    {
        nameof(Table.SourceType), nameof(Table.SourceName), nameof(Table.SchemaName), nameof(Table.TableName),
        nameof(Table.DisplayName), nameof(Table.Description), nameof(Table.LongDescription), nameof(Table.Texts),
        nameof(Table.DocumentationSource), nameof(Table.Sensitivity), nameof(Table.RequiresFourEyes), nameof(Table.IsActive)
    };

    private static TableMetadata FullyPopulatedExisting(TableIdentifier id) => new()
    {
        Identifier = id,
        OwnerTenantId = "tenant-a",
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceType = "SqlServer",
            SourceName = "d",
            SchemaName = "s",
            TableName = "t",
            DisplayName = "Persisted",
            Description = "persisted description",
            LongDescription = "persisted long description",
            Texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry> { ["de"] = new("Tabelle", "Beschreibung") }),
            DocumentationSource = "Admin",
            Sensitivity = "HIGH",
            RequiresFourEyes = true,
            IsActive = true,
            DataSourceType = DataSourceType.HttpPlugin,
            EnforcementMode = EnforcementMode.Delegated,
            PhysicalDatabaseName = "physical_db",
            Location = "s3://bucket/t",
            HttpEndpoint = new HttpEndpointDescriptor { BaseUrl = "https://api.local" },
            PluginName = "plugin",
            Tags = ["finance"],
            Visibility = DatasetVisibility.Full,
            RlsExpression = "region = 'EU'",
            MinGroupSize = 11,
            SmallGroupStrategy = SmallGroupStrategy.MergeIntoOther,
            RequiredFilterColumns = ImmutableArray.Create("a"),
            RequiredFilterColumnGroups = ImmutableArray.Create(ImmutableArray.Create("a", "b")),
            ExpectedRows = 1_000_000
        },
        Columns =
        [
            new TableColumn
            {
                Id = Guid.NewGuid(),
                ColumnName = "a",
                DisplayName = "Column A",
                DataType = "int",
                IsSensitive = true,
                Description = "persisted a",
                LongDescription = "persisted long a",
                Texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry> { ["de"] = new("Spalte A") }),
                DocumentationSource = "Admin",
                Meta = new Dictionary<string, string> { ["role"] = "dimension" }
            },
            new TableColumn { Id = Guid.NewGuid(), ColumnName = "b", DisplayName = "Column B", DataType = "varchar" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new MaskingRule { RuleType = "REDACT" }
        },
        PrimaryKeyColumns = ["a"],
        RowFilterEvaluation = new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 60, 100),
        CatalogTags = ["dbt"],
        CatalogView = CatalogView.Summary,
        ReleaseProfileToCatalogViewers = true,
        ProfilePublicationPolicy = DatasetProfilePublicationPolicy.FullCoarsenedProfile,
        PredefinedSyntheticSamples = [new Dictionary<string, object?> { ["a"] = 1 }],
        EstimatedRowCount = 42,
        ExpectedRows = 43,
        MinGroupSize = 7,
        SmallGroupStrategy = SmallGroupStrategy.Suppress,
        DeletedKeyPolicy = DeletedKeyPolicy.AllDeletedKeys
    };

    /// <summary>SEC-RATCHET-01: a catalog snapshot only carries catalog fields; it must not wipe anything else on the next sync.</summary>
    [Fact]
    public void Merge_RoundTrip_PreservesEveryNonCatalogTableProperty()
    {
        var id = new TableIdentifier("d", "s", "t");
        var existing = FullyPopulatedExisting(id);
        var incoming = new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceName = "d", SchemaName = "s", TableName = "t", DisplayName = "t", DocumentationSource = "DataCatalog" },
            Columns = [new TableColumn { ColumnName = "a", DataType = "int" }, new TableColumn { ColumnName = "b", DataType = "varchar" }]
        };

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        var defaults = new Table();
        foreach (var property in typeof(Table).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod != null))
        {
            if (CatalogOwnedTableProperties.Contains(property.Name))
            {
                continue;
            }

            var expected = property.GetValue(existing.Table);

            // Guard: the fixture sets every non-catalog property to a non-default value, so a new property cannot slip through.
            if (property.Name != nameof(Table.Id))
            {
                Equals(expected, property.GetValue(defaults)).ShouldBeFalse($"fixture must set a non-default value for Table.{property.Name}");
            }

            Equals(property.GetValue(merged.Table), expected).ShouldBeTrue($"Table.{property.Name} must survive the merge");
        }

        merged.RequiredFilterColumns.SequenceEqual(new[] { "a" }).ShouldBeTrue();
        merged.RequiredFilterColumnGroups.Single().SequenceEqual(new[] { "a", "b" }).ShouldBeTrue();
        merged.Table.EnforcementMode.ShouldBe(EnforcementMode.Delegated);

        // Catalog-owned fields without a catalog value keep the persisted value; governance flags are not weakened.
        merged.Table.Texts.ShouldBeSameAs(existing.Table.Texts);
        merged.Table.Description.ShouldBe("persisted description");
        merged.Table.LongDescription.ShouldBe("persisted long description");
        merged.Table.Sensitivity.ShouldBe("HIGH");
        merged.Table.RequiresFourEyes.ShouldBeTrue();
        merged.Table.DocumentationSource.ShouldBe("DataCatalog");

        // Column properties the catalog does not deliver survive.
        var colA = merged.Columns.Single(c => c.ColumnName == "a");
        var exA = existing.Columns.Single(c => c.ColumnName == "a");
        colA.Id.ShouldBe(exA.Id);
        colA.DisplayName.ShouldBe("Column A");
        colA.Texts.ShouldBeSameAs(exA.Texts);
        colA.IsSensitive.ShouldBeTrue();
        colA.Description.ShouldBe("persisted a");
        colA.LongDescription.ShouldBe("persisted long a");
        colA.DocumentationSource.ShouldBe("Admin");
        colA.Meta["role"].ShouldBe("dimension");
        merged.Columns.Single(c => c.ColumnName == "b").DisplayName.ShouldBe("Column B");

        // Metadata-level settings survive.
        merged.OwnerTenantId.ShouldBe("tenant-a");
        merged.ColumnMaskingRules["a"].RuleType.ShouldBe("REDACT");
        merged.PrimaryKeyColumns.ShouldBe(["a"]);
        merged.RowFilterEvaluation.ShouldBe(existing.RowFilterEvaluation);
        merged.CatalogTags.ShouldBe(["dbt"]);
        merged.ReleaseProfileToCatalogViewers.ShouldBeTrue();
        merged.ProfilePublicationPolicy.ShouldBe(DatasetProfilePublicationPolicy.FullCoarsenedProfile);
        merged.PredefinedSyntheticSamples.Count.ShouldBe(1);
        merged.EstimatedRowCount.ShouldBe(42);
        merged.ExpectedRows.ShouldBe(43);
        merged.MinGroupSize.ShouldBe(7);
        merged.SmallGroupStrategy.ShouldBe(SmallGroupStrategy.Suppress);
    }

    [Fact]
    public void Merge_CatalogValuesForCatalogOwnedFields_StillApply()
    {
        var id = new TableIdentifier("d", "s", "t");
        var existing = FullyPopulatedExisting(id);
        var incomingTexts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry> { ["en"] = new("Table") });
        var incoming = new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceName = "d", SchemaName = "s", TableName = "t", DisplayName = "From catalog", Description = "catalog description", Texts = incomingTexts },
            Columns = [new TableColumn { ColumnName = "a", DataType = "bigint", DisplayName = "Catalog A", Description = "catalog a" }]
        };

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        merged.Table.DisplayName.ShouldBe("From catalog");
        merged.Table.Description.ShouldBe("catalog description");
        merged.Table.Texts.ShouldBeSameAs(incomingTexts);
        merged.Table.RequiredFilterColumns.SequenceEqual(new[] { "a" }).ShouldBeTrue();
        var colA = merged.Columns.Single(c => c.ColumnName == "a");
        colA.DataType.ShouldBe("bigint");
        colA.DisplayName.ShouldBe("Catalog A");
        colA.Description.ShouldBe("catalog a");
        colA.IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void Merge_KeepsStoredRequiredFilterColumns_WhenIncomingHasNone()
    {
        var id = new TableIdentifier("d", "s", "t");
        var existing = FullyPopulatedExisting(id);
        var incoming = new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceName = "d", SchemaName = "s", TableName = "t" },
            Columns = [new TableColumn { ColumnName = "a" }]
        };

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        merged.RequiredFilterColumns.SequenceEqual(new[] { "a" }).ShouldBeTrue();
    }

    [Fact]
    public void Merge_UnionsRequiredFilterColumnGroups()
    {
        var id = new TableIdentifier("d", "s", "t");
        var existing = FullyPopulatedExisting(id); // has group ["a", "b"]
        var incoming = new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = "d", SchemaName = "s", TableName = "t",
                RequiredFilterColumnGroups = ImmutableArray.Create(ImmutableArray.Create("c", "d"))
            },
            Columns = [new TableColumn { ColumnName = "a" }, new TableColumn { ColumnName = "b" }, new TableColumn { ColumnName = "c" }, new TableColumn { ColumnName = "d" }]
        };

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        merged.RequiredFilterColumnGroups.Length.ShouldBe(2);
        merged.RequiredFilterColumnGroups[0].SequenceEqual(new[] { "a", "b" }).ShouldBeTrue();
        merged.RequiredFilterColumnGroups[1].SequenceEqual(new[] { "c", "d" }).ShouldBeTrue();
    }

    [Fact]
    public void Merge_KeepsMinGroupSizeAndSmallGroupStrategy()
    {
        var id = new TableIdentifier("d", "s", "t");
        var existing = FullyPopulatedExisting(id); // MinGroupSize = 11 on Table, 7 on Metadata; SmallGroupStrategy = MergeIntoOther on Table, Suppress on Metadata
        var incoming = new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = "d", SchemaName = "s", TableName = "t",
                MinGroupSize = 5, // smaller than existing 11
                SmallGroupStrategy = SmallGroupStrategy.MergeToOther
            },
            Columns = [new TableColumn { ColumnName = "a" }]
        };

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        merged.Table.MinGroupSize.ShouldBe(11); // Max(11, 5) = 11
        merged.Table.SmallGroupStrategy.ShouldBe(SmallGroupStrategy.MergeIntoOther); // existing kept
        merged.MinGroupSize.ShouldBe(7); // metadata-level governance kept
        merged.SmallGroupStrategy.ShouldBe(SmallGroupStrategy.Suppress);
    }

    [Fact]
    public void Merge_KeepsOwnerTenantAndReleasePolicies()
    {
        var id = new TableIdentifier("d", "s", "t");
        var existing = FullyPopulatedExisting(id);
        var incoming = new TableMetadata
        {
            Identifier = id,
            OwnerTenantId = "tenant-other",
            ReleaseProfileToCatalogViewers = false,
            ProfilePublicationPolicy = DatasetProfilePublicationPolicy.FormatAndEmptyShareOnly,
            DeletedKeyPolicy = DeletedKeyPolicy.KnownKeysOnly,
            Table = new Table { SourceName = "d", SchemaName = "s", TableName = "t" },
            Columns = [new TableColumn { ColumnName = "a" }]
        };

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        merged.OwnerTenantId.ShouldBe("tenant-a");
        merged.ReleaseProfileToCatalogViewers.ShouldBeTrue();
        merged.ProfilePublicationPolicy.ShouldBe(DatasetProfilePublicationPolicy.FullCoarsenedProfile);
        merged.DeletedKeyPolicy.ShouldBe(DeletedKeyPolicy.AllDeletedKeys);
    }

    private static readonly HashSet<string> CatalogOwnedMetadataProperties = new(StringComparer.Ordinal)
    {
        nameof(TableMetadata.Table),
        nameof(TableMetadata.Identifier),
        nameof(TableMetadata.Columns),
        nameof(TableMetadata.ColumnMaskingRules),
        nameof(TableMetadata.PrimaryKeyColumns),
        nameof(TableMetadata.Indexes),
        nameof(TableMetadata.ReplaceKeyDefinition),
        nameof(TableMetadata.CatalogTags),
        nameof(TableMetadata.EstimatedRowCount),
        nameof(TableMetadata.ExpectedRows)
    };

    [Fact]
    public void Merge_CopiesOrAllowListsEveryTableAndMetadataProperty()
    {
        var id = new TableIdentifier("d", "s", "t");
        var existing = FullyPopulatedExisting(id);
        var incoming = new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceName = "d", SchemaName = "s", TableName = "t" },
            Columns = [new TableColumn { ColumnName = "a", DataType = "int" }, new TableColumn { ColumnName = "b", DataType = "varchar" }]
        };

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        // 1. Table properties
        var tableProperties = typeof(Table).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod != null);

        foreach (var property in tableProperties)
        {
            if (CatalogOwnedTableProperties.Contains(property.Name))
            {
                continue;
            }

            var expected = property.GetValue(existing.Table);
            var actual = property.GetValue(merged.Table);

            if (property.Name != nameof(Table.Id))
            {
                Equals(expected, property.GetValue(new Table())).ShouldBeFalse(
                    $"fixture must set a non-default value for Table.{property.Name}");
            }

            Equals(actual, expected).ShouldBeTrue($"Table.{property.Name} must survive the merge without drift");
        }

        // 2. TableMetadata properties
        var metadataProperties = typeof(TableMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod != null);

        foreach (var property in metadataProperties)
        {
            if (CatalogOwnedMetadataProperties.Contains(property.Name))
            {
                continue;
            }

            var expected = property.GetValue(existing);
            var actual = property.GetValue(merged);

            Equals(expected, property.GetValue(new TableMetadata())).ShouldBeFalse(
                $"fixture must set a non-default value for TableMetadata.{property.Name}");

            Equals(actual, expected).ShouldBeTrue(
                $"TableMetadata.{property.Name} must survive the merge without drift");
        }
    }
}
