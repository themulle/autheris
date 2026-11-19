namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autheris.Modules.Dbt;
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
}
