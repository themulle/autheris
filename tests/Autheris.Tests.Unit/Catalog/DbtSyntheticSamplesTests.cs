namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Profiling;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Shouldly;
using Xunit;

public sealed class DbtSyntheticSamplesTests
{
    [Fact]
    public async Task ParseManifest_WithInlineSyntheticSamples_ExtractsSamples()
    {
        var manifestJson = """
        {
          "nodes": {
            "model.analytics.dim_customers": {
              "name": "dim_customers",
              "database": "db",
              "schema": "analytics",
              "config": { "materialized": "table" },
              "meta": {
                "synthetic_samples": [
                  { "customer_id": 1001, "email": "anna@example.com", "country": "DE" },
                  { "customer_id": 1002, "email": "ben@example.com", "country": "AT" }
                ]
              },
              "columns": {
                "customer_id": { "name": "customer_id", "data_type": "int", "meta": {}, "tags": [] },
                "email": { "name": "email", "data_type": "varchar", "meta": {}, "tags": [] },
                "country": { "name": "country", "data_type": "varchar", "meta": {}, "tags": [] }
              },
              "depends_on": { "nodes": [] }
            }
          }
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
        var models = await DbtArtifactStreamingParser.ParseManifestStreamAsync(stream);
        var model = models.Single(m => m.Name == "dim_customers");

        model.SyntheticSamples.ShouldNotBeNull();
        model.SyntheticSamples!.Count.ShouldBe(2);
        model.SyntheticSamples[0]["customer_id"].ShouldBe(1001L);
        model.SyntheticSamples[0]["email"].ShouldBe("anna@example.com");
        model.SyntheticSamples[1]["country"].ShouldBe("AT");
    }

    [Fact]
    public void Generator_WithPredefinedSamples_MaskColumnGetsPlaceholder_AndExcludesDeniedColumns()
    {
        var tableId = new TableIdentifier("sales", "dbo", "customers");
        var table = new TableMetadata
        {
            Identifier = tableId,
            Columns =
            [
                new TableColumn { ColumnName = "customer_id", DataType = "int" },
                new TableColumn { ColumnName = "email", DataType = "varchar", IsSensitive = true },
                new TableColumn { ColumnName = "secret_token", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["email"] = new MaskingRule { RuleType = "REDACT" }
            },
            PredefinedSyntheticSamples =
            [
                new Dictionary<string, object?>
                {
                    ["customer_id"] = 999,
                    ["email"] = "curated_mock@example.com",
                    ["secret_token"] = "internal_sample"
                }
            ]
        };

        var generator = new SyntheticSampleRowGenerator();

        var accessLevels = new Dictionary<string, ColumnAccessLevel>
        {
            ["customer_id"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Mask,
            ["secret_token"] = ColumnAccessLevel.Deny
        };

        var rows = generator.GenerateRows(table, count: 1, accessLevels);

        rows.Count.ShouldBe(1);
        var row = rows[0];

        row["__synthetic"].ShouldBe(true);
        row["customer_id"].ShouldBe(999);
        // MCP-03: predefined values are emitted only for Clear columns; a Mask column gets the safe placeholder.
        row["email"].ShouldBe("synthetic.redacted.1@example.invalid");
        // Denied column is completely stripped:
        row.ContainsKey("secret_token").ShouldBeFalse();
    }

    private static TableMetadata PredefinedTable() => new()
    {
        Identifier = new TableIdentifier("sales", "dbo", "customers"),
        Columns =
        [
            new TableColumn { ColumnName = "customer_id", DataType = "int" },
            new TableColumn { ColumnName = "email", DataType = "varchar", IsSensitive = true },
            new TableColumn { ColumnName = "note", DataType = "varchar" }
        ],
        PredefinedSyntheticSamples =
        [
            new Dictionary<string, object?> { ["customer_id"] = 999, ["email"] = "real@corp.example", ["note"] = "curated" }
        ]
    };

    [Fact]
    [Trait("Category", "Security")]
    public void GenerateRows_Predefined_MaskColumn_EmitsPlaceholder()
    {
        // MCP-03
        var levels = new Dictionary<string, ColumnAccessLevel>
        {
            ["customer_id"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Mask,
            ["note"] = ColumnAccessLevel.Clear
        };

        var row = new SyntheticSampleRowGenerator().GenerateRows(PredefinedTable(), 1, levels)[0];

        row["email"].ShouldNotBe("real@corp.example");
        row["note"].ShouldBe("curated");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GenerateRows_Predefined_SensitiveColumnNoEntry_NotEmitted()
    {
        // MCP-03: a partial map never results in Clear; the sensitive column is missing from it.
        var levels = new Dictionary<string, ColumnAccessLevel>
        {
            ["customer_id"] = ColumnAccessLevel.Clear,
            ["note"] = ColumnAccessLevel.Clear
        };

        var row = new SyntheticSampleRowGenerator().GenerateRows(PredefinedTable(), 1, levels)[0];

        row.ContainsKey("email").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GenerateRows_Predefined_ExplicitClearOnSensitiveColumn_BecomesMask()
    {
        // MCP-03
        var levels = new Dictionary<string, ColumnAccessLevel>
        {
            ["customer_id"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Clear,
            ["note"] = ColumnAccessLevel.Clear
        };

        var row = new SyntheticSampleRowGenerator().GenerateRows(PredefinedTable(), 1, levels)[0];

        row["email"].ShouldBe("synthetic.redacted.1@example.invalid");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GenerateRows_NullLevelMap_EmitsOnlySyntheticMarker()
    {
        // MCP-03 condition 3: a null map means every column is Deny, for predefined and generated rows alike.
        var generator = new SyntheticSampleRowGenerator();

        var predefined = generator.GenerateRows(PredefinedTable(), 1, columnAccessLevels: null)[0];
        predefined.Count.ShouldBe(1);
        predefined.ContainsKey("__synthetic").ShouldBeTrue();

        var generatedTable = PredefinedTable() with { PredefinedSyntheticSamples = null };
        var generated = generator.GenerateRows(generatedTable, 1, columnAccessLevels: null)[0];
        generated.Count.ShouldBe(1);
        generated.ContainsKey("__synthetic").ShouldBeTrue();
    }
}
