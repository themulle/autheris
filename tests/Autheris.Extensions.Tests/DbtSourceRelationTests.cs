namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Shouldly;
using Xunit;

/// <summary>Issue #392: dbt relations and keys declared on sources; reference resolution, ambiguity and relationships_combination.</summary>
public sealed class DbtSourceRelationTests
{
    private const string Crm = "source.shop.crm.customers";
    private const string Orders = "source.shop.crm.orders";
    private const string Erp = "source.shop.erp.customers";

    private static object[] Cols(params string[] names) => names.Cast<object>().ToArray();

    private static Dictionary<string, object> Columns(params string[] names) =>
        names.ToDictionary(c => c, c => (object)new Dictionary<string, object> { ["name"] = c, ["data_type"] = "integer" });

    private static Dictionary<string, object> Mdl(string name, string package = "p", string[]? cols = null, object[]? constraints = null, string[]? dependsOn = null, object? version = null)
    {
        var node = new Dictionary<string, object>
        {
            ["name"] = name,
            ["package_name"] = package,
            ["database"] = "db",
            ["schema"] = "s",
            ["columns"] = Columns(cols ?? ["id"])
        };
        if (constraints is not null) node["constraints"] = constraints;
        if (dependsOn is not null) node["depends_on"] = new Dictionary<string, object> { ["nodes"] = dependsOn };
        if (version is not null) node["version"] = version;
        return node;
    }

    private static Dictionary<string, object> Src(string source, string table, string db = "db") => new()
    {
        ["name"] = table,
        ["source_name"] = source,
        ["package_name"] = "shop",
        ["database"] = db,
        ["schema"] = "raw",
        ["columns"] = Columns("customer_id", "tenant_id", "order_id")
    };

    private static Dictionary<string, object> Test(string testName, Dictionary<string, object> kwargs, string[] dependsOn, string? attached = null, string? ns = null)
    {
        var meta = new Dictionary<string, object> { ["name"] = testName, ["kwargs"] = kwargs };
        if (ns is not null) meta["namespace"] = ns;
        var node = new Dictionary<string, object>
        {
            ["resource_type"] = "test",
            ["test_metadata"] = meta,
            ["depends_on"] = new Dictionary<string, object> { ["nodes"] = dependsOn }
        };
        if (attached is not null) node["attached_node"] = attached;
        return node;
    }

    private static Dictionary<string, object> Rel(string to, string childColumn = "customer_id", string field = "customer_id") =>
        new() { ["column_name"] = childColumn, ["to"] = to, ["field"] = field };

    private static MemoryStream Manifest(Dictionary<string, object>? nodes = null, Dictionary<string, object>? sources = null)
    {
        var root = new Dictionary<string, object> { ["metadata"] = new Dictionary<string, object> { ["project_name"] = "shop" } };
        if (nodes is not null) root["nodes"] = nodes;
        if (sources is not null) root["sources"] = sources;
        return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    private static Dictionary<string, object> TwoSources() => new()
    {
        [Crm] = Src("crm", "customers", "db_a"),
        [Orders] = Src("crm", "orders", "db_a"),
        [Erp] = Src("erp", "customers", "db_b")
    };

    private static Task<DbtManifestParseResult> Parse(MemoryStream stream) => DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(stream);

    private static MemoryStream Fixture() =>
        new(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "manifest-sources-only-392.json")));

    // ------------------------------------------------------------------ 392a: sources

    [Fact]
    public async Task Parse_SourceRelationshipsTest_ResolvesParentAndChildUniqueIds()
    {
        using var stream = Fixture();
        var result = await Parse(stream);

        result.Warnings.ShouldBeEmpty();
        var single = result.Relationships.Single(r => r.Name == "customers");
        single.ParentUniqueId.ShouldBe(Crm);
        single.ChildUniqueId.ShouldBe(Orders);
        single.ParentModelOrTable.ShouldBe("customers");
        single.ChildModelOrTable.ShouldBe("orders");
        single.ParentColumns.ShouldBe(["customer_id"]);
        single.ChildColumns.ShouldBe(["customer_id"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Parse_SourceRelationshipsTest_ParentFirstInDependsOn_ChildIsNotParent()
    {
        // I392-T-02 (I392-SC-01/04): the parent is the first source in depends_on; it must never become the child.
        using var stream = Manifest(
            new() { ["test.shop.r"] = Test("relationships", Rel("source('crm', 'customers')"), [Crm, Orders]) },
            TwoSources());

        var result = await Parse(stream);

        var rel = result.Relationships.ShouldHaveSingleItem();
        rel.ParentUniqueId.ShouldBe(Crm);
        rel.ChildUniqueId.ShouldBe(Orders);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Parse_SameTableNameInTwoDatabases_ResolvesBySourceName()
    {
        // I392-T-02: crm.customers (db_a) and erp.customers (db_b) share a table name; source_name decides.
        using var stream = Manifest(
            new()
            {
                ["test.shop.r_erp"] = Test("relationships", Rel("source('erp', 'customers')"), [Erp, Orders]),
                ["test.shop.r_crm"] = Test("relationships", Rel("source('crm', 'customers')", "order_id"), [Crm, Orders])
            },
            TwoSources());

        var result = await Parse(stream);

        result.Relationships.Count.ShouldBe(2);
        result.Relationships.Single(r => r.ParentUniqueId == Erp).ChildUniqueId.ShouldBe(Orders);
        result.Relationships.Single(r => r.ParentUniqueId == Crm).ChildUniqueId.ShouldBe(Orders);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Parse_RefMatchingTwoPackages_NoNarrowing_SkipsAmbiguousReference()
    {
        // I392-T-01 (I392-SC-02): the owner package a owns one candidate and is still not preferred.
        using var stream = Manifest(new()
        {
            ["model.a.dim"] = Mdl("dim", "a"),
            ["model.b.dim"] = Mdl("dim", "b"),
            ["model.a.fct"] = Mdl("fct", "a"),
            ["test.a.r"] = Test("relationships", Rel("ref('dim')"), ["model.a.fct"], attached: "model.a.fct")
        });

        var result = await Parse(stream);

        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldContain(w => w.EndsWith("skipped: AMBIGUOUS_REFERENCE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parse_RefMatchingTwoPackages_DependsOnOrExplicitPackageResolves()
    {
        using var viaDepends = Manifest(new()
        {
            ["model.a.dim"] = Mdl("dim", "a"),
            ["model.b.dim"] = Mdl("dim", "b"),
            ["model.a.fct"] = Mdl("fct", "a"),
            ["test.a.r"] = Test("relationships", Rel("ref('dim')"), ["model.a.fct", "model.b.dim"], attached: "model.a.fct")
        });
        using var viaPackage = Manifest(new()
        {
            ["model.a.dim"] = Mdl("dim", "a"),
            ["model.b.dim"] = Mdl("dim", "b"),
            ["model.a.fct"] = Mdl("fct", "a"),
            ["test.a.r"] = Test("relationships", Rel("ref('a', 'dim')"), ["model.a.fct"], attached: "model.a.fct")
        });

        (await Parse(viaDepends)).Relationships.ShouldHaveSingleItem().ParentUniqueId.ShouldBe("model.b.dim");
        (await Parse(viaPackage)).Relationships.ShouldHaveSingleItem().ParentUniqueId.ShouldBe("model.a.dim");
    }

    [Fact]
    public async Task Parse_RefWithVersion_FiltersByVersion()
    {
        using var stream = Manifest(new()
        {
            ["model.p.dim.v1"] = Mdl("dim", version: 1),
            ["model.p.dim.v2"] = Mdl("dim", version: 2),
            ["model.p.fct"] = Mdl("fct"),
            ["test.p.r"] = Test("relationships", Rel("ref('dim', v=2)"), ["model.p.fct"], attached: "model.p.fct")
        });

        (await Parse(stream)).Relationships.ShouldHaveSingleItem().ParentUniqueId.ShouldBe("model.p.dim.v2");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Parse_TwoCandidateChildren_SkipsAmbiguousReference()
    {
        using var stream = Manifest(
            new() { ["test.shop.r"] = Test("relationships", Rel("source('crm', 'customers')"), [Crm, Orders, Erp]) },
            TwoSources());

        var result = await Parse(stream);

        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: AMBIGUOUS_REFERENCE");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Parse_SelfRelationInference_OnlyWhenSingleDependsOn(int dependsOnCount)
    {
        // I392-T-03 (I392-SC-04): self relation only when depends_on holds exactly the parent; with two other nodes it is ambiguous.
        var deps = dependsOnCount == 1 ? new[] { Crm } : [Crm, Orders, Erp];
        using var stream = Manifest(
            new() { ["test.shop.r"] = Test("relationships", Rel("source('crm', 'customers')", "customer_id", "tenant_id"), deps) },
            TwoSources());

        var result = await Parse(stream);

        if (dependsOnCount == 1)
        {
            var rel = result.Relationships.ShouldHaveSingleItem();
            rel.ChildUniqueId.ShouldBe(Crm);
            rel.ParentUniqueId.ShouldBe(Crm);
        }
        else
        {
            result.Relationships.ShouldBeEmpty();
            result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: AMBIGUOUS_REFERENCE");
        }
    }

    [Fact]
    public async Task Parse_UnknownSource_SkipsUnknownModel()
    {
        using var stream = Manifest(
            new() { ["test.shop.r"] = Test("relationships", Rel("source('crm', 'ghost')"), [Orders]) },
            TwoSources());

        var result = await Parse(stream);

        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: UNKNOWN_MODEL");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Parse_AttachedNodeNotInIndex_SkipsUnknownModel()
    {
        // I392-T-04 (I392-SC-03): the attached node must be indexed and listed in depends_on.
        using var notIndexed = Manifest(
            new() { ["test.shop.r"] = Test("relationships", Rel("source('crm', 'customers')"), [Crm, "model.shop.ghost"], attached: "model.shop.ghost") },
            TwoSources());
        using var notInDependsOn = Manifest(
            new() { ["test.shop.r"] = Test("relationships", Rel("source('crm', 'customers')"), [Crm], attached: Orders) },
            TwoSources());

        foreach (var stream in new[] { notIndexed, notInDependsOn })
        {
            var result = await Parse(stream);
            result.Relationships.ShouldBeEmpty();
            result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: UNKNOWN_MODEL");
        }
    }

    [Fact]
    public async Task Parse_ModelForeignKeyToSource_Resolves()
    {
        using var stream = Manifest(
            new()
            {
                ["model.p.stg_orders"] = Mdl("stg_orders", cols: ["customer_id"], dependsOn: [Crm], constraints:
                [
                    new Dictionary<string, object> { ["type"] = "foreign_key", ["to"] = "source('crm', 'customers')", ["columns"] = Cols("customer_id"), ["to_columns"] = Cols("customer_id") }
                ])
            },
            TwoSources());

        var result = await Parse(stream);

        var rel = result.Relationships.ShouldHaveSingleItem();
        rel.ParentUniqueId.ShouldBe(Crm);
        rel.ChildUniqueId.ShouldBe("model.p.stg_orders");
        rel.ChildModelOrTable.ShouldBe("stg_orders");
    }

    [Fact]
    public async Task Parse_ModelRelationships_NamesUnchanged()
    {
        using var stream = Manifest(new()
        {
            ["model.p.stg_customers"] = Mdl("stg_customers"),
            ["model.p.fct_orders"] = Mdl("fct_orders", constraints:
            [
                new Dictionary<string, object> { ["type"] = "foreign_key", ["to"] = "ref('stg_customers')", ["columns"] = Cols("id"), ["to_columns"] = Cols("id") }
            ])
        });

        var rel = (await Parse(stream)).Relationships.ShouldHaveSingleItem();

        rel.Name.ShouldBe("customers");
        rel.ParentModelOrTable.ShouldBe("stg_customers");
        rel.ChildModelOrTable.ShouldBe("fct_orders");
        rel.ParentUniqueId.ShouldBe("model.p.stg_customers");
        rel.ChildUniqueId.ShouldBe("model.p.fct_orders");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Parse_PendingRelations_CountedAtCollection_LimitHolds()
    {
        // I392-T-10 (I392-SC-08): unresolved relations are counted when collected; memory and warnings stay bounded.
        var nodes = new Dictionary<string, object>();
        for (var i = 0; i < 10_300; i++)
        {
            nodes[$"test.shop.r{i}"] = Test("relationships", Rel("source('crm', 'ghost')"), [Orders]);
        }

        using var stream = Manifest(nodes, TwoSources());
        var result = await Parse(stream);

        result.Relationships.ShouldBeEmpty();
        result.Warnings.Count.ShouldBeLessThanOrEqualTo(10_001);
        result.Warnings.ShouldContain(w => w.Contains("TOO_MANY_RELATIONS", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ 392a: source keys

    [Fact]
    public async Task Parse_UniqueTestOnSource_DeclaresKey()
    {
        using var stream = Fixture();
        var result = await Parse(stream);

        var orders = result.Models.Single(m => m.UniqueId == Orders);
        orders.DeclaredKeys.ShouldNotBeNull();
        orders.DeclaredKeys!.UniqueConstraints.ShouldContain(i => i.SequenceEqual(new[] { "order_id" }));
    }

    [Fact]
    public async Task Parse_UniqueCombinationOnSource_DeclaresKey()
    {
        using var stream = Fixture();
        var result = await Parse(stream);

        var customers = result.Models.Single(m => m.UniqueId == Crm);
        customers.DeclaredKeys.ShouldNotBeNull();
        customers.DeclaredKeys!.UniqueConstraints.ShouldContain(i => i.SequenceEqual(new[] { "customer_id" }));
        customers.DeclaredKeys.UniqueConstraints.ShouldContain(i => i.SequenceEqual(new[] { "customer_id", "tenant_id" }));
        result.Models.Single(m => m.UniqueId == Erp).DeclaredKeys.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Parse_KeyTestWithTwoDependencies_SkipsAmbiguousAttachment()
    {
        // I392-T-03 (I392-SC-04): two eligible dependencies and no attached_node declare no key.
        using var stream = Manifest(
            new()
            {
                ["test.shop.u"] = Test("unique", new() { ["column_name"] = "customer_id" }, [Crm, Orders])
            },
            TwoSources());

        var result = await Parse(stream);

        result.Models.ShouldAllBe(m => m.DeclaredKeys == null);
        result.Warnings.ShouldHaveSingleItem().ShouldBe("Key test 'test.shop.u' skipped: AMBIGUOUS_ATTACHMENT");
    }

    // ------------------------------------------------------------------ 392b: relationships_combination

    private static Dictionary<string, object> Combination(object? columns, object? fields, string to = "source('crm', 'customers')", string? relationName = null)
    {
        var kwargs = new Dictionary<string, object>();
        if (columns is not null) kwargs["columns"] = columns;
        if (fields is not null) kwargs["fields"] = fields;
        kwargs["to"] = to;
        if (relationName is not null) kwargs["relation_name"] = relationName;
        return kwargs;
    }

    [Fact]
    public async Task Parse_RelationshipsCombination_KeepsAllPairsInOrder()
    {
        using var stream = Fixture();
        var result = await Parse(stream);

        var rel = result.Relationships.Single(r => r.Name == "customer_tenant");
        rel.ChildColumns.ShouldBe(["customer_id", "tenant_id"]);
        rel.ParentColumns.ShouldBe(["customer_id", "tenant_id"]);
        rel.ParentUniqueId.ShouldBe(Crm);
        rel.ChildUniqueId.ShouldBe(Orders);
    }

    [Fact]
    public async Task Parse_RelationshipsCombination_CountMismatch_Skips()
    {
        using var stream = Manifest(
            new() { ["test.shop.c"] = Test("relationships_combination", Combination(Cols("customer_id", "tenant_id"), Cols("customer_id")), [Orders, Crm]) },
            TwoSources());

        var result = await Parse(stream);

        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: COUNT_MISMATCH");
    }

    [Fact]
    public async Task Parse_RelationshipsCombination_NonArrayColumns_UnsupportedForm()
    {
        using var stream = Manifest(
            new() { ["test.shop.c"] = Test("relationships_combination", Combination("customer_id", Cols("customer_id")), [Orders, Crm]) },
            TwoSources());

        var result = await Parse(stream);

        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: UNSUPPORTED_FORM");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Parse_RelationshipsCombination_ForeignNamespace_UnsupportedForm()
    {
        // I392-T-09 (I392-SC-08): a package test of the same name may mean something else.
        using var stream = Manifest(
            new() { ["test.shop.c"] = Test("relationships_combination", Combination(Cols("customer_id"), Cols("customer_id")), [Orders, Crm], ns: "other_pkg") },
            TwoSources());

        var result = await Parse(stream);

        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: UNSUPPORTED_FORM");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Parse_RelationshipsCombination_TooWide_Skips()
    {
        var wide = Enumerable.Range(0, RelationKeyRules.MaxKeyColumns + 5).Select(i => $"c{i}").ToArray();
        using var stream = Manifest(
            new() { ["test.shop.c"] = Test("relationships_combination", Combination(Cols(wide), Cols(wide)), [Orders, Crm]) },
            TwoSources());

        var result = await Parse(stream);

        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: TOO_WIDE");
    }

    [Fact]
    public async Task Parse_RelationshipsCombination_OwnNamespaceAccepted()
    {
        using var stream = Manifest(
            new() { ["test.shop.c"] = Test("relationships_combination", Combination(Cols("customer_id"), Cols("customer_id")), [Orders, Crm], ns: "shop") },
            TwoSources());

        (await Parse(stream)).Relationships.ShouldHaveSingleItem();
    }
}
