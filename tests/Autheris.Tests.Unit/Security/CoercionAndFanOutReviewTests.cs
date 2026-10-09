namespace Autheris.Tests.Unit.Security;

using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>Review M-9 (nested unpaginated list cost), E-5 (mandatory tenant column), E-6 (no coercion / LIKE wildcards in evaluators).</summary>
public sealed class CoercionAndFanOutReviewTests
{
    private static readonly TableIdentifier Table = new("crm", "dbo", "customers");

    private static TableMetadata Meta(params (string Name, string Type)[] columns) => new()
    {
        Identifier = Table,
        Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList()
    };

    private static List<IReadOnlyDictionary<string, object?>> Rows(params object?[] values) =>
        values.Select((v, i) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = i + 1, ["code"] = v }).ToList();

    // ---------------------------------------------------------------- E-6 in-memory FilterRows

    [Fact]
    public void FilterRows_QuotedLiteralAgainstIntColumn_FailsClosed()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["id"] = 7 } };

        GatewayExecutionService.FilterRows(rows, "[id] = '007'", Meta(("id", "int"))).ShouldBeEmpty();
        GatewayExecutionService.FilterRows(rows, "[id] IN ('7')", Meta(("id", "int"))).ShouldBeEmpty();
        GatewayExecutionService.FilterRows(rows, "[id] = 7", Meta(("id", "int"))).Count.ShouldBe(1);
    }

    [Fact]
    public void FilterRows_NumberLiteralAgainstTextColumn_FailsClosed()
    {
        var rows = Rows("7", "007");

        GatewayExecutionService.FilterRows(rows, "[code] = 7", Meta(("id", "int"), ("code", "varchar"))).ShouldBeEmpty();
        GatewayExecutionService.FilterRows(rows, "[code] = '7'", Meta(("id", "int"), ("code", "varchar"))).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("[code] LIKE '*'")]
    [InlineData("[code] LIKE 'a[b]%'")]
    public void FilterRows_DataTableOnlyLikeWildcards_FailClosed(string filter)
    {
        GatewayExecutionService.FilterRows(Rows("abc"), filter, Meta(("id", "int"), ("code", "varchar"))).ShouldBeEmpty();
    }

    [Fact]
    public void FilterRows_PlainLikePrefix_StillWorks()
    {
        GatewayExecutionService.FilterRows(Rows("abc", "xyz"), "[code] LIKE 'ab%'", Meta(("id", "int"), ("code", "varchar"))).Count.ShouldBe(1);
    }

    // ---------------------------------------------------------------- E-6 streaming evaluator

    [Fact]
    public void Streaming_StringIsNotCoercedToNumber()
    {
        var payload = new Dictionary<string, object?> { ["code"] = "007", ["n"] = 7 };

        StreamingRowFilterAstEvaluator.Matches(payload, "code = '7'").ShouldBeFalse();
        StreamingRowFilterAstEvaluator.Matches(payload, "code = 7").ShouldBeFalse();
        StreamingRowFilterAstEvaluator.Matches(payload, "n = '7'").ShouldBeFalse();
        StreamingRowFilterAstEvaluator.Matches(payload, "code IN (7)").ShouldBeFalse();
        StreamingRowFilterAstEvaluator.Matches(payload, "code BETWEEN 1 AND 10").ShouldBeFalse();
        StreamingRowFilterAstEvaluator.Matches(payload, "code = '007'").ShouldBeTrue();
        StreamingRowFilterAstEvaluator.Matches(payload, "n = 7").ShouldBeTrue();
        StreamingRowFilterAstEvaluator.Matches(payload, "n IN (7, 8)").ShouldBeTrue();
    }

    [Fact]
    public void Streaming_StringComparisonIsOrdinal()
    {
        var payload = new Dictionary<string, object?> { ["region"] = "eu" };

        StreamingRowFilterAstEvaluator.Matches(payload, "region = 'EU'").ShouldBeFalse();
        StreamingRowFilterAstEvaluator.Matches(payload, "region IN ('EU')").ShouldBeFalse();
        StreamingRowFilterAstEvaluator.Matches(payload, "region = 'eu'").ShouldBeTrue();
    }

    // ---------------------------------------------------------------- E-6 vector filters

    // ---------------------------------------------------------------- E-5 mandatory tenant column

    [Fact]
    public void RequireTenantColumn_TableWithoutColumn_IsRefused()
    {
        var meta = Meta(("id", "int"));

        Should.Throw<SecurityException>(() => TableMetadata.RequireTenantColumnOrThrow(meta, true, null));
        TableMetadata.RequireTenantColumnOrThrow(meta, false, null).ShouldBeNull();
    }

    [Fact]
    public void RequireTenantColumn_ExemptTableAndTenantTable_Pass()
    {
        TableMetadata.RequireTenantColumnOrThrow(Meta(("id", "int")), true, new[] { "DBO.Customers" }).ShouldBeNull();
        TableMetadata.RequireTenantColumnOrThrow(Meta(("id", "int")), true, new[] { "customers" }).ShouldBeNull();
        TableMetadata.RequireTenantColumnOrThrow(Meta(("id", "int"), ("TenantId", "varchar")), true, null).ShouldBe("TenantId");
    }

    // ---------------------------------------------------------------- M-9

    public sealed class GrandChild
    {
        public string Name { get; set; } = "";
    }

    public sealed class Child
    {
        public string Name { get; set; } = "";
        public List<GrandChild> Items { get; set; } = new();
    }

    public sealed class RootObject
    {
        public List<Child> Children { get; set; } = new();
    }

    public sealed class RootQuery
    {
        public RootObject Root => new();
        public List<Child> GetItems(int first = 1) => new();
    }

    [Fact]
    public async Task UnpaginatedNestedLists_CostGrowsMultiplicatively()
    {
        var schema = await new ServiceCollection().AddGraphQLServer().AddQueryType<RootQuery>().BuildSchemaAsync();
        var rule = new QueryCostAnalyzerRule(maxAllowedCost: int.MaxValue);

        var one = rule.ComputeCost(Utf8GraphQLParser.Parse("{ root { children { name } } }"), schema);
        var two = rule.ComputeCost(Utf8GraphQLParser.Parse("{ root { children { name items { name } } } }"), schema);
        var twoDeep = rule.ComputeCost(Utf8GraphQLParser.Parse("{ root { children { items { name } } } }"), schema);

        one.ShouldBeGreaterThanOrEqualTo(10);
        two.ShouldBeGreaterThan(one * 5);
        twoDeep.ShouldBeGreaterThanOrEqualTo(100);
    }

    [Fact]
    public async Task QueryCostAnalyzer_WithRuntimeVariables_CalculatesCostFromActualCoercedValue()
    {
        var schema = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<RootQuery>()
            .BuildSchemaAsync();

        var query = Utf8GraphQLParser.Parse("query MyQuery($n: Int = 1) { items(first: $n) { name } }");

        // Without runtime variables: uses default $n = 1
        int costDefault = QueryCostAnalyzerRule.CalculateCost(query, schema);

        // With runtime variables: $n = 500
        var runtimeVars = new Dictionary<string, object?> { ["n"] = 500 };
        int costRuntime = QueryCostAnalyzerRule.CalculateCost(query, schema, variableValues: runtimeVars);

        costRuntime.ShouldBeGreaterThan(costDefault * 10);
    }
}

