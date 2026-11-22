using Autheris.Application.Catalog;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Catalog;

[Trait("Category", "Security")]
public sealed class CatalogProtectionProjectionTests
{
    [Fact]
    public void Table_model_has_no_sensitivity_or_four_eyes_properties()
    {
        var tableType = typeof(Table);
        tableType.GetProperty("Sensitivity").ShouldBeNull();
        tableType.GetProperty("RequiresFourEyes").ShouldBeNull();
        tableType.GetProperty("IsHighlySensitive").ShouldBeNull();
        tableType.GetMethod("SensitivityRank").ShouldBeNull();
        tableType.GetMethod("IsSensitivityHigh").ShouldBeNull();
    }

    [Fact]
    public void Persisted_projection_excludes_retired_sensitivity_columns()
    {
        CatalogTablePersistedProjection.CoveredProperties.ShouldNotContain("Table.Sensitivity");
        CatalogTablePersistedProjection.CoveredProperties.ShouldNotContain("Table.RequiresFourEyes");
        CatalogTablePersistedProjection.PersistedSqlColumns.Keys.ShouldNotContain("TABLES.sensitivity");
        CatalogTablePersistedProjection.PersistedSqlColumns.Keys.ShouldNotContain("TABLES.requires_four_eyes");
    }

    [Fact]
    public void Identical_catalog_tables_project_as_unchanged()
    {
        var meta = new TableMetadata
        {
            Identifier = new TableIdentifier("crm", "public", "customers"),
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceType = "PostgreSQL",
                SourceName = "crm",
                SchemaName = "public",
                TableName = "customers",
                DisplayName = "customers",
                IsActive = true,
                MinGroupSize = 5
            },
            Columns = [new TableColumn { ColumnName = "email", DataType = "varchar" }]
        };

        var proj = CatalogTablePersistedProjection.From(meta);
        var diff = CatalogTablePersistedProjection.Compare(proj, proj);
        diff.Classification.ShouldBe(CatalogWriteClassification.Unchanged);
    }
}
