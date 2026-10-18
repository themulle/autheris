using System.Security.Claims;
using System.Text.RegularExpressions;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#112 A-1 / SEC-112-20 / C-10: the generated SDL (T-5.1, T-5.2, T-5.3, T-S-17).</summary>
public sealed class TypedAggregateSdlTests
{
    private static string TypeBlock(string sdl, string name)
    {
        var match = Regex.Match(sdl, $@"(type|input|enum|interface) {Regex.Escape(name)}\b[^{{]*\{{(?<body>[^}}]*)\}}", RegexOptions.Singleline);
        match.Success.ShouldBeTrue($"type {name} not found");
        return match.Groups["body"].Value;
    }

    private static async Task<string> SdlAsync(TypedAggregateTestHost? host = null) =>
        (await (host ?? new TypedAggregateTestHost()).BuildAsync()).Schema.ToString();

    [Fact]
    public async Task AggregateField_HasExactlyTheTypedArguments_NoStringWhereNoStringGroupBy()
    {
        var sdl = await SdlAsync();

        var start = sdl.IndexOf("sales_dbo_orders_aggregate(", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        var signature = sdl[start..sdl.IndexOf("sales_dbo_orders_aggregate_result!", start, StringComparison.Ordinal)];
        signature.ShouldContain("groupBy: [sales_dbo_orders_column!]");
        signature.ShouldContain("aggregates: [sales_dbo_orders_aggregate_input!]!");
        signature.ShouldContain("where: sales_dbo_orders_filter");
        signature.ShouldContain("orderBy: [sales_dbo_orders_aggregate_order_by!]");
        signature.ShouldContain("limit: Int = 100");
        signature.ShouldNotContain("where: String");
        signature.ShouldNotContain("groupBy: [String!]");
        signature.ShouldNotContain("MinGroupSize");
        signature.ShouldNotContain("Strategy");
    }

    [Fact]
    public async Task OldUntypedResult_IsGone()
    {
        var sdl = await SdlAsync();

        sdl.ShouldNotContain("AutherisAggregationResult");
        sdl.ShouldNotContain("groupsJson");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task GraphQLSchema_HasNoSuppressedRowsCountField_SEC_112_20()
    {
        var sdl = await SdlAsync();

        TypeBlock(sdl, "sales_dbo_orders_aggregate_result").ShouldNotContain("suppressedRowsCount");
        TypeBlock(sdl, "AutherisMetricQueryResult").ShouldNotContain("suppressedRowsCount");
        TypeBlock(sdl, "sales_dbo_orders_aggregate_result").ShouldContain("suppressedGroupsCount: Int!");
        sdl.ShouldNotContain("suppressedRowsCount");
    }

    [Fact]
    public async Task ColumnEnum_ValuesEqualTheEntityFields_ReservedNamesSuffixed()
    {
        var sdl = await SdlAsync();

        var entity = Regex.Matches(TypeBlock(sdl, "sales_dbo_orders"), @"^\s*(\w+)[:(]", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var values = Regex.Matches(TypeBlock(sdl, "sales_dbo_orders_column"), @"^\s*(\w+)\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();

        values.ShouldContain("null_col");
        values.ShouldNotContain("null");
        values.Select(v => v == "null_col" ? "null" : v).ShouldBe(entity.Where(e => e != "orders"));
    }

    [Fact]
    public async Task InputsAndGroupTypes_HaveTheDocumentedShape()
    {
        var sdl = await SdlAsync();

        TypeBlock(sdl, "sales_dbo_orders_aggregate_input").ShouldContain("fn: AutherisAggregateFn!");
        TypeBlock(sdl, "sales_dbo_orders_aggregate_input").ShouldContain("column: sales_dbo_orders_column");
        TypeBlock(sdl, "sales_dbo_orders_aggregate_input").ShouldContain("alias: String!");
        TypeBlock(sdl, "sales_dbo_orders_aggregate_order_by").ShouldContain("direction: AutherisSortDirection! = ASC");
        TypeBlock(sdl, "sales_dbo_orders_aggregate_group").ShouldContain("key: sales_dbo_orders_aggregate_key!");
        TypeBlock(sdl, "sales_dbo_orders_aggregate_group").ShouldContain("aggregates: [AutherisAggregateValue!]!");
        TypeBlock(sdl, "AutherisAggregateValue").ShouldContain("kind: AutherisValueKind!");
        TypeBlock(sdl, "AutherisAggregateValue").ShouldNotContain("booleanValue");
        TypeBlock(sdl, "AutherisValueKind").ShouldContain("DATE_TIME");
        TypeBlock(sdl, "AutherisAggregateFn").ShouldContain("COUNT_DISTINCT");
    }

    [Fact]
    public async Task KeyType_UsesTheEntityScalarMapping_AllNullable()
    {
        var sdl = await SdlAsync();
        var key = TypeBlock(sdl, "sales_dbo_orders_aggregate_key");

        key.ShouldContain("amount: Decimal");
        key.ShouldContain("qty: Int");
        key.ShouldContain("big: Long");
        key.ShouldContain("ratio: Float");
        key.ShouldContain("is_vip: Boolean");
        key.ShouldContain("created: String");
        key.ShouldNotContain("!");
    }

    [Fact]
    public async Task QueryMetrics_IsTyped_NoStringWhere()
    {
        var sdl = await SdlAsync();

        var start = sdl.IndexOf("queryMetrics(", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        var signature = sdl[start..sdl.IndexOf("AutherisMetricQueryResult!", start, StringComparison.Ordinal)];
        signature.ShouldContain("where: AutherisMetricFilter");
        signature.ShouldContain("orderBy: [AutherisMetricOrderBy!]");
        signature.ShouldNotContain("where: String");
        signature.ShouldNotContain("orderBy: [String!]");
        sdl.ShouldContain("input AutherisTypedFilter @oneOf");
        TypeBlock(sdl, "AutherisMetricGroup").ShouldContain("values: [AutherisAggregateValue!]!");
        TypeBlock(sdl, "AutherisMetricDefinition").ShouldNotContain("filter");
        TypeBlock(sdl, "AutherisMetricDefinition").ShouldNotContain("meta");
    }

    [Fact]
    public async Task Schema_IsIdenticalForAllCallers_AndIncludesTheAggregateTypes()
    {
        var alice = new TypedAggregateTestHost();
        var bob = new TypedAggregateTestHost
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "bob"), new Claim("tenant_id", "T2")], "TestAuth"))
        };

        var aliceSdl = await SdlAsync(alice);
        var bobSdl = await SdlAsync(bob);

        bobSdl.ShouldBe(aliceSdl);
        aliceSdl.ShouldContain("sales_dbo_orders_aggregate_result");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task AggregateTypes_ExistExactlyForTablesWithEntityTypes_C10()
    {
        var host = new TypedAggregateTestHost
        {
            Tables = [TypedAggregateTestHost.DefaultTable(), TypedAggregateTestHost.DefaultTable("empty", ("bad-col", "int"))]
        };
        var executor = await host.BuildAsync();

        executor.Schema.Types.ContainsName("sales_dbo_orders_aggregate_result").ShouldBeTrue();
        executor.Schema.Types.ContainsName("sales_dbo_empty").ShouldBeFalse();
        executor.Schema.Types.ContainsName("sales_dbo_empty_aggregate_result").ShouldBeFalse();
        executor.Schema.Types.ContainsName("sales_dbo_empty_column").ShouldBeFalse();
        var result = await executor.ExecuteAsync(HotChocolate.Execution.OperationRequestBuilder.New().SetDocument("{ sales_dbo_empty_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }]) { count } }").Build());
        TypedAggregateTestHost.Errors(result).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AggregateTypes_HaveNoRelationFields()
    {
        var sdl = await SdlAsync();

        foreach (var name in new[] { "sales_dbo_orders_aggregate_key", "sales_dbo_orders_aggregate_group", "sales_dbo_orders_aggregate_result" })
        {
            TypeBlock(sdl, name).ShouldNotContain("(first");
        }
    }

    [Fact]
    public async Task SdlGrowth_For50TablesAnd20Columns_StaysBelowTheRecordedBound()
    {
        var columns = Enumerable.Range(0, 20).Select(i => ($"c{i}", i % 2 == 0 ? "int" : "varchar(10)")).ToArray();
        var withoutAggregates = new TypedAggregateTestHost
        {
            AggregationEnabled = false,
            Tables = Enumerable.Range(0, 50).Select(i => TypedAggregateTestHost.DefaultTable($"t{i}", columns)).ToList()
        };
        var withAggregates = new TypedAggregateTestHost
        {
            Tables = Enumerable.Range(0, 50).Select(i => TypedAggregateTestHost.DefaultTable($"t{i}", columns)).ToList()
        };

        var before = (await SdlAsync(withoutAggregates)).Length;
        var after = (await SdlAsync(withAggregates)).Length;

        // Recorded 2026-10-18 (risk R-2): six typed members per table add well under 4x the base schema for this catalog.
        (after - before).ShouldBeLessThan(before * 4);
        (after - before).ShouldBeLessThan(2_000_000);
    }

    [Fact]
    public async Task Fingerprint_ChangesWhenAColumnTypeChanges()
    {
        var a = await TypedAggregateSchemaTests.BuildAsync(TypedAggregateTestHost.DefaultTable("t", ("id", "int"), ("x", "int")));
        var b = await TypedAggregateSchemaTests.BuildAsync(TypedAggregateTestHost.DefaultTable("t", ("id", "int"), ("x", "varchar(5)")));
        var c = await TypedAggregateSchemaTests.BuildAsync(TypedAggregateTestHost.DefaultTable("t", ("id", "int"), ("x", "int")));

        a.Fingerprint().ShouldNotBe(b.Fingerprint());
        a.Fingerprint().ShouldBe(c.Fingerprint());
    }

    [Trait("Category", "Security")]
    [Fact]
    public void GraphQlProject_HasNoFromTrinoText_B7()
    {
        var root = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "Autheris.GraphQL"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("FromTrinoText", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void RemovedAnalyticsQueryExtensions_IsGone()
    {
        typeof(Autheris.GraphQL.Types.Query).Assembly.GetType("Autheris.GraphQL.Types.AnalyticsQueryExtensions").ShouldBeNull();
    }

    [Fact]
    public async Task GroupByFieldsEnum_ExistsInSchema()
    {
        var sdl = await SdlAsync();
        TypeBlock(sdl, "sales_dbo_orders_group_by_fields").ShouldNotBeNull();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
