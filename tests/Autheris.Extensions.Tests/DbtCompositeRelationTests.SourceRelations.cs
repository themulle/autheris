namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Shouldly;
using Xunit;

/// <summary>Issue #392: ingestion of relations between sources, fail-closed model lookup and tenant filter by unique id.</summary>
public sealed partial class DbtCompositeRelationTests
{
    private static MemoryStream SourcesOnly() =>
        new(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "manifest-sources-only-392.json")));

    [Fact]
    public async Task Ingest_SourcesOnlyManifest_ImportsBothRelationsAndKeys()
    {
        var h = new Harness();
        using var stream = SourcesOnly();

        var result = await h.Service.IngestManifestStreamAsync(stream);

        result.Success.ShouldBeTrue();
        result.ImportedRelationsCount.ShouldBe(2);
        result.Warnings.ShouldNotContain(w => w.Contains("UNKNOWN_MODEL", StringComparison.Ordinal));
        result.Warnings.ShouldNotContain(w => w.Contains("PARENT_KEY_NOT_UNIQUE", StringComparison.Ordinal));
        var single = h.Written.Single(r => r.RelationName == "customers");
        single.ParentTableIdentifier.ShouldBe(new TableIdentifier("db_a", "raw", "customers"));
        single.ChildTableIdentifier.ShouldBe(new TableIdentifier("db_a", "raw", "orders"));
        var multi = h.Written.Single(r => r.RelationName == "customer_tenant");
        multi.JoinKeysChild.ShouldBe(["customer_id", "tenant_id"]);
        multi.JoinKeysParent.ShouldBe(["customer_id", "tenant_id"]);
    }

    [Fact]
    public async Task Ingest_SourcesOnlyManifest_SecondSync_IsIdempotent()
    {
        var h = new Harness();
        using var first = SourcesOnly();
        using var second = SourcesOnly();

        await h.Service.IngestManifestStreamAsync(first);
        await h.Service.IngestManifestStreamAsync(second);

        // The natural key (parent, child, name) and the pairs are identical, so the repository upsert finds the same rows.
        h.Written.Count.ShouldBe(4);
        string Key(TableRelation r) => $"{r.ParentTableIdentifier}|{r.ChildTableIdentifier}|{r.RelationName}|{string.Join(',', r.JoinKeysParent)}|{string.Join(',', r.JoinKeysChild)}";
        h.Written.Take(2).Select(Key).OrderBy(x => x).ShouldBe(h.Written.Skip(2).Select(Key).OrderBy(x => x));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Lookup_NameOnlyRelation_TwoModelsSameName_ReportsAmbiguousModel()
    {
        // I392-T-05 (I392-SC-01): a legacy relation (no unique ids) never picks one of two models with the same name.
        var a = Def("model.a.dim", "dim", "db_a");
        var b = Def("model.b.dim", "dim", "db_b");
        var c = Def("model.a.fct", "fct", "db_a");
        var lookup = new DbtRelationModelLookup([a, b, c]);

        var code = lookup.TryResolve(new DbtRelationshipDefinition("dim", "dim", "fct", ["id"], ["id"]), out var parent, out var child);

        code.ShouldBe("AMBIGUOUS_MODEL");
        parent.ShouldBeNull();
        child.ShouldBeNull();
        lookup.TryResolve(new DbtRelationshipDefinition("c", "fct", "fct", ["id"], ["id"]), out var p2, out _).ShouldBeNull();
        p2.ShouldBe(c);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Lookup_UniqueIdNotInModels_NoNameFallback()
    {
        // I392-T-05 (I392-SC-03): a unique id that is not in the model list fails even if the name exists.
        var lookup = new DbtRelationModelLookup([Def("model.a.dim", "dim", "db_a"), Def("model.a.fct", "fct", "db_a")]);

        lookup.TryResolve(new DbtRelationshipDefinition("dim", "dim", "fct", ["id"], ["id"], "snapshot.a.dim", "model.a.fct"), out _, out _)
            .ShouldBe("UNKNOWN_MODEL");
        lookup.TryResolve(new DbtRelationshipDefinition("dim", "dim", "fct", ["id"], ["id"], "model.a.dim", null), out _, out _)
            .ShouldBe("UNKNOWN_MODEL");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Ingest_UniqueIdNotInModels_NoNameFallback()
    {
        // I392-T-05: the parent is a snapshot (indexed for ref(), but not a catalog model); a model with the same name must not stand in.
        var h = new Harness();
        using var stream = Manifest(new Dictionary<string, object>
        {
            ["snapshot.p.dim_orders"] = Node("dim_orders", ["a"]),
            ["model.p.fct_lines"] = Node("fct_lines", ["x"], [Fk("dim_orders", ["x"], ["a"])])
        });

        var result = await h.Service.IngestManifestStreamAsync(stream);

        h.Written.ShouldBeEmpty();
        result.ImportedRelationsCount.ShouldBe(0);
        result.Warnings.ShouldContain(w => w.EndsWith("skipped: UNKNOWN_MODEL", StringComparison.Ordinal) || w.EndsWith("skipped: UNKNOWN_MODEL", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Ingest_RelationToRejectedTenantByUniqueId_Dropped_ReasonDoesNotNameForeignNode()
    {
        // I392-T-06 (I392-SC-07): erp.customers lives in db_b, outside the caller's tenant db_a.
        var h = new Harness();
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["metadata"] = new Dictionary<string, object> { ["project_name"] = "shop" },
            ["nodes"] = new Dictionary<string, object>
            {
                ["test.shop.r"] = new Dictionary<string, object>
                {
                    ["test_metadata"] = new Dictionary<string, object>
                    {
                        ["name"] = "relationships",
                        ["kwargs"] = new Dictionary<string, object> { ["column_name"] = "customer_id", ["to"] = "source('erp', 'secret_parent')", ["field"] = "customer_id" }
                    },
                    ["depends_on"] = new Dictionary<string, object> { ["nodes"] = new[] { "source.shop.erp.secret_parent", "source.shop.crm.orders" } }
                }
            },
            ["sources"] = new Dictionary<string, object>
            {
                ["source.shop.erp.secret_parent"] = SourceNode("erp", "secret_parent", "db_b"),
                ["source.shop.crm.orders"] = SourceNode("crm", "orders", "db_a")
            }
        });
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        var result = await h.Service.IngestManifestStreamAsync(stream, tenant: new TenantId("db_a"));

        result.ImportedRelationsCount.ShouldBe(0);
        h.Written.ShouldBeEmpty();
        var dropped = result.Warnings.Single(w => w.StartsWith("A relationship was dropped", StringComparison.Ordinal));
        dropped.ShouldNotContain("secret_parent");
        dropped.ShouldNotContain("db_b");
    }

    [Fact]
    public async Task Ingest_CombinationWithoutName_SamePairAsSingleColumn_SkipsDuplicateRelation()
    {
        // Without relation_name both tests get the default name and collide: fail closed.
        var h = new Harness();
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["metadata"] = new Dictionary<string, object> { ["project_name"] = "shop" },
            ["nodes"] = new Dictionary<string, object>
            {
                ["test.shop.single"] = RelTest("relationships", new() { ["column_name"] = "customer_id", ["to"] = "source('crm', 'customers')", ["field"] = "customer_id" }),
                ["test.shop.combo"] = RelTest("relationships_combination", new()
                {
                    ["columns"] = new[] { "customer_id", "tenant_id" },
                    ["to"] = "source('crm', 'customers')",
                    ["fields"] = new[] { "customer_id", "tenant_id" }
                })
            },
            ["sources"] = new Dictionary<string, object>
            {
                ["source.shop.crm.customers"] = SourceNode("crm", "customers", "db_a"),
                ["source.shop.crm.orders"] = SourceNode("crm", "orders", "db_a")
            }
        });
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        var result = await h.Service.IngestManifestStreamAsync(stream);

        h.Written.ShouldBeEmpty();
        result.Warnings.ShouldContain(w => w.EndsWith("skipped: DUPLICATE_RELATION", StringComparison.Ordinal));
    }

    private static Dictionary<string, object> RelTest(string name, Dictionary<string, object> kwargs) => new()
    {
        ["test_metadata"] = new Dictionary<string, object> { ["name"] = name, ["kwargs"] = kwargs },
        ["depends_on"] = new Dictionary<string, object> { ["nodes"] = new[] { "source.shop.crm.customers", "source.shop.crm.orders" } }
    };

    private static Dictionary<string, object> SourceNode(string source, string table, string db) => new()
    {
        ["name"] = table,
        ["source_name"] = source,
        ["package_name"] = "shop",
        ["database"] = db,
        ["schema"] = "raw",
        ["columns"] = new Dictionary<string, object>
        {
            ["customer_id"] = new Dictionary<string, object> { ["name"] = "customer_id" },
            ["tenant_id"] = new Dictionary<string, object> { ["name"] = "tenant_id" }
        }
    };

    private static DbtModelDefinition Def(string uniqueId, string name, string database) =>
        new(uniqueId, name, database, "s", "table", null, [], new Dictionary<string, string>(), new Dictionary<string, DbtColumnDefinition>(), [], false);
}
