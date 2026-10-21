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
    public async Task ParseManifest_WithExternalJsonFileReference_ExtractsSamples()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"synthetic_test_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(tempFile, """
            [
              { "id": 42, "code": "ABC-123" }
            ]
            """);

            var manifestJson = $$"""
            {
              "nodes": {
                "model.analytics.orders": {
                  "name": "orders",
                  "database": "db",
                  "schema": "analytics",
                  "config": { "materialized": "table" },
                  "meta": {
                    "synthetic_samples": "{{tempFile.Replace("\\", "/")}}"
                  },
                  "columns": {
                    "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] }
                  },
                  "depends_on": { "nodes": [] }
                }
              }
            }
            """;

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
            var models = await DbtArtifactStreamingParser.ParseManifestStreamAsync(stream);
            var model = models.Single(m => m.Name == "orders");

            model.SyntheticSamples.ShouldNotBeNull();
            model.SyntheticSamples!.Count.ShouldBe(1);
            model.SyntheticSamples[0]["id"].ShouldBe(42L);
            model.SyntheticSamples[0]["code"].ShouldBe("ABC-123");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void Generator_WithPredefinedSamples_DoesNotMaskValues_AndExcludesDeniedColumns()
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
        // Requirement: "Sample Daten sollten nicht maskiert werden"
        // The mock email remains as curated in plain text, NOT redacted:
        row["email"].ShouldBe("curated_mock@example.com");
        // Denied column is completely stripped:
        row.ContainsKey("secret_token").ShouldBeFalse();
    }
}
