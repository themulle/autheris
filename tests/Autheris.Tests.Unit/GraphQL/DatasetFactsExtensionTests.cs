using HotChocolate;
using System.Text.Json;
using Autheris.Domain.Common;
using HotChocolate.Execution;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.DatasetFactsTestHost;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>Opt-in <c>extensions.autheris.roots</c>: per root field the dataset, tags, row-filter flag and truncation.</summary>
[Collection("CatalogRelation")]
public sealed class DatasetFactsExtensionTests
{
    private const string TwoRoots = "{ a: sales_dbo_customers { id } b: sales_dbo_orders { order_id } }";

    [Fact]
    public async Task TwoAliasedRoots_ReportDatasetTagsAndRowFilterPerResponseName()
    {
        var host = new DatasetFactsTestHost();
        host.Tree.RowFilters[Customers] = [FixedDescription];
        host.VisibleTables = [Table(Customers, ["pii", "crm"]), Table(Orders)];
        var executor = await host.BuildAsync();

        var roots = Roots(await RunAsync(executor, TwoRoots))!.Value;

        var a = roots.GetProperty("a");
        a.GetProperty("dataset").GetString().ShouldBe("sales.dbo.customers");
        a.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ShouldBe(["pii", "crm"]);
        a.GetProperty("rowFilterApplied").GetBoolean().ShouldBeTrue();
        a.GetProperty("rowFilters").EnumerateArray().Select(f => f.GetProperty("description").GetString()).ShouldBe([FixedDescription]);
        a.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        a.GetProperty("relations").GetArrayLength().ShouldBe(0);
        var b = roots.GetProperty("b");
        b.GetProperty("dataset").GetString().ShouldBe("sales.dbo.orders");
        b.GetProperty("rowFilterApplied").GetBoolean().ShouldBeFalse();
        b.GetProperty("rowFilters").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task RelationRead_IsListedWithPathAndOwnFacts()
    {
        var host = new DatasetFactsTestHost();
        host.Tree.RowFilters[Orders] = [FixedDescription];
        host.VisibleTables = [Table(Customers), Table(Orders, ["finance"])];
        var executor = await host.BuildAsync();

        var roots = Roots(await RunAsync(executor, "{ c: sales_dbo_customers { id o: orders { note } } }"))!.Value;

        var relation = roots.GetProperty("c").GetProperty("relations").EnumerateArray().Single();
        relation.GetProperty("path").GetString().ShouldBe("c.o");
        relation.GetProperty("dataset").GetString().ShouldBe("sales.dbo.orders");
        relation.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ShouldBe(["finance"]);
        relation.GetProperty("rowFilterApplied").GetBoolean().ShouldBeTrue();
        roots.GetProperty("c").GetProperty("rowFilterApplied").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task OnlyCutListRoot_IsTruncated()
    {
        var host = new DatasetFactsTestHost();
        host.VisibleTables = [Table(Customers), Table(Orders)];
        var executor = await host.BuildAsync();

        var roots = Roots(await RunAsync(executor, "{ cut: sales_dbo_customers(first: 1) { id } whole: sales_dbo_orders { order_id } }"))!.Value;

        roots.GetProperty("cut").GetProperty("truncated").GetBoolean().ShouldBeTrue();
        roots.GetProperty("whole").GetProperty("truncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Connection_HasNextPage_IsTruncated()
    {
        var host = new DatasetFactsTestHost();
        host.Tree.RowJson[Customers] = """[{"id":1},{"id":2},{"id":3}]""";
        host.VisibleTables = [Table(Customers), Table(Orders)];
        var executor = await host.BuildAsync();

        var result = await RunAsync(executor, "{ p: sales_dbo_customersConnection(first: 2) { edges { node { id } } } }");

        Roots(result)!.Value.GetProperty("p").GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task WithoutFlag_ResponseIsByteIdentical()
    {
        var withMiddleware = new DatasetFactsTestHost();
        withMiddleware.VisibleTables = [Table(Customers), Table(Orders)];
        var without = new DatasetFactsTestHost { UseMiddleware = false };
        without.VisibleTables = [Table(Customers), Table(Orders)];

        var a = await RunAsync(await withMiddleware.BuildAsync(), TwoRoots, autherisExtension: null);
        var b = await RunAsync(await without.BuildAsync(), TwoRoots, autherisExtension: null);

        a.ToJson().ShouldBe(b.ToJson());
    }

    [Fact]
    public async Task FlagFalse_NoAutherisRoots()
    {
        var host = new DatasetFactsTestHost();
        host.VisibleTables = [Table(Customers), Table(Orders)];

        var result = await RunAsync(await host.BuildAsync(), TwoRoots, new Dictionary<string, object?> { ["datasetFacts"] = false });

        Roots(result).ShouldBeNull();
    }

    [Fact]
    public async Task ExistingIndexHints_ArePreserved()
    {
        var host = new DatasetFactsTestHost();
        host.VisibleTables = [Table(Customers), Table(Orders)];
        var executor = await host.BuildAsync();
        var request = OperationRequestBuilder.New().SetDocument(TwoRoots)
            .SetExtensions(new Dictionary<string, object?> { ["autheris"] = new Dictionary<string, object?> { ["datasetFacts"] = true } })
            .Build();
        var result = (await executor.ExecuteAsync(request)).ExpectOperationResult();

        // The facts middleware merges into the autheris dictionary: other keys written by inner middleware must survive.
        var ext = result.Extensions!["autheris"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!;
        ext.ContainsKey("roots").ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Extension_NotPresentWithoutFlag()
    {
        var host = new DatasetFactsTestHost();
        host.VisibleTables = [Table(Customers), Table(Orders)];

        var result = await RunAsync(await host.BuildAsync(), TwoRoots, autherisExtension: null);

        Roots(result).ShouldBeNull();
        result.ToJson().ShouldNotContain("datasetFacts");
        result.ToJson().ShouldNotContain("sales.dbo.customers");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DeniedRoot_NullsData_AndWritesNoRootsAtAll()
    {
        var host = new DatasetFactsTestHost();
        host.Tree.Denied.Add(Orders);
        host.VisibleTables = [Table(Customers), Table(Orders)];

        var result = await RunAsync(await host.BuildAsync(), TwoRoots);

        Json(result).RootElement.TryGetProperty("errors", out _).ShouldBeTrue("the denied root must raise an error");
        Roots(result).ShouldBeNull();
        result.ToJson().ShouldNotContain("sales.dbo");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RootWithFieldErrorPath_HasNoEntry()
    {
        var host = new DatasetFactsTestHost();
        host.InjectedError = ErrorBuilder.New().SetMessage("field failed").SetPath(HotChocolate.Path.Root.Append("a").Append(0).Append("orders")).Build();
        host.VisibleTables = [Table(Customers), Table(Orders)];

        var result = await RunAsync(await host.BuildAsync(), "{ a: sales_dbo_customers { id orders { note } } b: sales_dbo_orders { order_id } }");

        Json(result).RootElement.TryGetProperty("errors", out _).ShouldBeTrue("the scenario must raise a field error below root a");
        var roots = Roots(result)!.Value;
        roots.TryGetProperty("a", out _).ShouldBeFalse();
        roots.TryGetProperty("b", out _).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ErrorWithoutPath_WritesNoRoots()
    {
        var host = new DatasetFactsTestHost { InjectedError = ErrorBuilder.New().SetMessage("no path").Build() };
        host.VisibleTables = [Table(Customers), Table(Orders)];

        var result = await RunAsync(await host.BuildAsync(), TwoRoots);

        Json(result).RootElement.TryGetProperty("errors", out _).ShouldBeTrue();
        Roots(result).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DataNull_WritesNoRoots()
    {
        var host = new DatasetFactsTestHost();
        host.Tree.Denied.Add(Customers);
        host.VisibleTables = [Table(Customers), Table(Orders)];

        var result = await RunAsync(await host.BuildAsync(), "{ a: sales_dbo_customers { id } }");

        Roots(result).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RootTableNotCatalogVisible_HasNoEntry()
    {
        var host = new DatasetFactsTestHost();
        host.VisibleTables = [Table(Orders)];

        var roots = Roots(await RunAsync(await host.BuildAsync(), TwoRoots))!.Value;

        roots.TryGetProperty("a", out _).ShouldBeFalse();
        roots.TryGetProperty("b", out _).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task InvisibleRootSucceededRead_HasNoEntryAndNoPlaceholder()
    {
        var host = new DatasetFactsTestHost();
        host.VisibleTables = [];

        var result = await RunAsync(await host.BuildAsync(), TwoRoots);

        var roots = Roots(result)!.Value;
        roots.EnumerateObject().ShouldBeEmpty();
        result.ToJson().ShouldNotContain("\"a\":null");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RelationTableNotCatalogVisible_IsNotListed()
    {
        var host = new DatasetFactsTestHost();
        host.VisibleTables = [Table(Customers)];

        var roots = Roots(await RunAsync(await host.BuildAsync(), "{ c: sales_dbo_customers { id orders { note } } }"))!.Value;

        roots.GetProperty("c").GetProperty("relations").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VisibilityServiceMissingOrThrowing_NoEntry()
    {
        var throwing = new DatasetFactsTestHost();
        throwing.VisibleTables = [Table(Customers), Table(Orders)];
        throwing.Visibility.Throws = true;

        var roots = Roots(await RunAsync(await throwing.BuildAsync(), TwoRoots))!.Value;

        roots.EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AuthoritativeFalseBatch_NoEntryWithoutRow()
    {
        var host = new DatasetFactsTestHost();
        host.VisibleTables = [Table(Orders)];
        host.Visibility.Authoritative = false;

        var roots = Roots(await RunAsync(await host.BuildAsync(), TwoRoots))!.Value;

        roots.TryGetProperty("a", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CancelledResolver_NoEntry()
    {
        var host = new DatasetFactsTestHost();
        host.Tree.Cancelled = true;
        host.VisibleTables = [Table(Customers), Table(Orders)];
        var executor = await host.BuildAsync();

        OperationResult? result = null;
        try
        {
            result = await RunAsync(executor, TwoRoots);
        }
        catch (OperationCanceledException)
        {
        }

        if (result is not null)
        {
            Roots(result).ShouldBeNull();
        }

        host.Visibility.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ClientSuppliedRootsExtension_IsNotEchoed()
    {
        var host = new DatasetFactsTestHost();
        host.VisibleTables = [Table(Customers), Table(Orders)];
        var supplied = new Dictionary<string, object?>
        {
            ["datasetFacts"] = true,
            ["roots"] = new Dictionary<string, object?> { ["evil"] = new Dictionary<string, object?> { ["dataset"] = "forged.db.table" } },
            ["other"] = "client-key"
        };

        var result = await RunAsync(await host.BuildAsync(), TwoRoots, supplied);

        var json = result.ToJson();
        json.ShouldNotContain("forged.db.table");
        json.ShouldNotContain("client-key");
        Roots(result)!.Value.TryGetProperty("evil", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"autheris":{"datasetFacts":"true"}}""")]
    [InlineData("""{"autheris":{"datasetFacts":1}}""")]
    [InlineData("""{"autheris":{"datasetFacts":null}}""")]
    [InlineData("""{"autheris":5}""")]
    [InlineData("""{"autheris":[true]}""")]
    [Trait("Category", "Security")]
    public async Task MalformedExtension_FlagFalse(string extensionJson)
    {
        var host = new DatasetFactsTestHost();
        host.VisibleTables = [Table(Customers), Table(Orders)];
        var executor = await host.BuildAsync();
        var request = OperationRequestBuilder.New().SetDocument(TwoRoots).SetExtensions(JsonDocument.Parse(extensionJson)).Build();

        var result = (await executor.ExecuteAsync(request)).ExpectOperationResult();

        Roots(result).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TagsAreCappedAndPlain()
    {
        var host = new DatasetFactsTestHost();
        var tags = Enumerable.Range(0, 100).Select(i => new string((char)('a' + i % 26), 200)).Append(" ").ToList();
        host.VisibleTables = [Table(Customers, tags), Table(Orders)];

        var roots = Roots(await RunAsync(await host.BuildAsync(), TwoRoots))!.Value;

        var emitted = roots.GetProperty("a").GetProperty("tags").EnumerateArray().ToList();
        emitted.Count.ShouldBe(64);
        emitted.ShouldAllBe(t => t.ValueKind == JsonValueKind.String && t.GetString()!.Length == 128);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RelationsCapped()
    {
        var host = new DatasetFactsTestHost();
        host.Tree.ExtraRelations = 250;
        host.VisibleTables = [Table(Customers), Table(Orders)];

        var roots = Roots(await RunAsync(await host.BuildAsync(), "{ a: sales_dbo_customers { id orders { note } } }"))!.Value;

        roots.GetProperty("a").GetProperty("relations").GetArrayLength().ShouldBe(200);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExtensionObjectsArePlainTypes()
    {
        var host = new DatasetFactsTestHost();
        host.Tree.RowFilters[Customers] = [FixedDescription];
        host.VisibleTables = [Table(Customers, ["pii"]), Table(Orders)];
        var executor = await host.BuildAsync();
        var request = OperationRequestBuilder.New().SetDocument("{ a: sales_dbo_customers { id orders { note } } }")
            .SetExtensions(new Dictionary<string, object?> { ["autheris"] = new Dictionary<string, object?> { ["datasetFacts"] = true } }).Build();

        var result = (await executor.ExecuteAsync(request)).ExpectOperationResult();

        var autheris = result.Extensions!["autheris"];
        AssertPlain(autheris);
        static void AssertPlain(object? value)
        {
            switch (value)
            {
                case null or string or bool or int:
                    break;
                case Dictionary<string, object?> map:
                    foreach (var v in map.Values)
                    {
                        AssertPlain(v);
                    }

                    break;
                case List<object?> list:
                    foreach (var v in list)
                    {
                        AssertPlain(v);
                    }

                    break;
                default:
                    throw new ShouldAssertException($"Non-plain extension value: {value.GetType().FullName}");
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ParallelRoots_NoLostOrCrossedEntries()
    {
        var host = new DatasetFactsTestHost();
        host.VisibleTables = [Table(Customers, ["pii"]), Table(Orders, ["finance"])];
        var fields = Enumerable.Range(0, 8).Select(i => i % 2 == 0 ? $"r{i}: sales_dbo_customers {{ id }}" : $"r{i}: sales_dbo_orders {{ order_id }}");

        var roots = Roots(await RunAsync(await host.BuildAsync(), "{ " + string.Join(' ', fields) + " }"))!.Value;

        for (var i = 0; i < 8; i++)
        {
            roots.GetProperty($"r{i}").GetProperty("dataset").GetString().ShouldBe(i % 2 == 0 ? "sales.dbo.customers" : "sales.dbo.orders");
        }

        host.Visibility.Calls.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Extension_ContainsNoRowValuesColumnNamesOrPredicates()
    {
        var host = new DatasetFactsTestHost();
        host.Tree.RowFilters[Customers] = [FixedDescription];
        host.VisibleTables = [Table(Customers), Table(Orders)];

        var result = await RunAsync(await host.BuildAsync(), "{ a: sales_dbo_customers { id email } }");

        var extensions = Json(result).RootElement.GetProperty("extensions").GetRawText();
        extensions.ShouldNotContain(SecretEmail);
        extensions.ShouldNotContain(SecretColumn);
        extensions.ShouldNotContain("region = ");
        extensions.ShouldNotContain("@p");
    }
}
