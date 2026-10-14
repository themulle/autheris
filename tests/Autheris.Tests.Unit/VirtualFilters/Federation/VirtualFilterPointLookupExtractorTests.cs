namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// F-GOV-14 WI-7 (M6): point lookups are taken only from the top-level AND chain of the WHERE clause of a single, simple
/// query specification, qualified for the protected table; every other shape yields no lookup (a scan, always safe).
/// </summary>
public sealed class VirtualFilterPointLookupExtractorTests
{
    private static readonly FastSqlEngine Engine = new();
    private static readonly TableMetadata Crm = HttpTable("crm_api", "public", "customers", "id", "name");
    private static readonly TableMetadata Erp = SqlTable("erp", "public", "orders", "id", "customer_id");

    private static IReadOnlyDictionary<string, string[]>? Extract(string sql)
    {
        var metadata = Engine.Analyze(sql.AsMemory());
        var tables = metadata.ReferencedTables
            .Select(t => t.TableName is "customers"
                ? new ResolvedSourceTable(t, Crm.Identifier, Crm, "crm_api")
                : new ResolvedSourceTable(t, Erp.Identifier, Erp, "erp"))
            .ToList();
        var (tree, _) = Engine.Parse(sql.AsMemory());
        var statement = new SqlAstBuilder().BuildStatement(tree);
        var lookup = VirtualFilterPointLookupExtractor.Extract(statement, tables, Crm.Identifier);
        return lookup?.ToDictionary(kv => kv.Key, kv => kv.Value.Select(v => v.Value.ToString()!).ToArray(), System.StringComparer.OrdinalIgnoreCase);
    }

    private const string From = " FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id ";

    [Fact]
    public void Equality_QualifiedWithTheAlias_IsExtracted()
    {
        Extract("SELECT c.id" + From + "WHERE c.id = 47")!["id"].ShouldBe(["47"]);
    }

    [Fact]
    public void Equality_LiteralOnTheLeft_IsExtracted()
    {
        Extract("SELECT c.id" + From + "WHERE '47' = c.id")!["id"].ShouldBe(["47"]);
    }

    [Fact]
    public void InList_YieldsAllValues()
    {
        Extract("SELECT c.id" + From + "WHERE c.id IN (1, 2)")!["id"].ShouldBe(["1", "2"]);
    }

    [Fact]
    public void Conjunction_ExtractsEveryConjunct()
    {
        var lookup = Extract("SELECT c.id" + From + "WHERE c.id = 1 AND (c.name = 'a') AND o.id = 5")!;

        lookup["id"].ShouldBe(["1"]);
        lookup["name"].ShouldBe(["a"]);
        lookup.Count.ShouldBe(2);   // o.id belongs to the other table
    }

    [Fact]
    public void ColumnOfAnotherTable_WithTheSameName_IsIgnored()
    {
        // ST-25 / AC-6.2: o.id = 47 must not become a lookup of the protected table.
        Extract("SELECT c.id" + From + "WHERE o.id = 47").ShouldBeEmpty();
    }

    [Fact]
    public void UnqualifiedColumn_WithTwoNamedTables_IsIgnored()
    {
        Extract("SELECT c.id" + From + "WHERE id = 47").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("WHERE c.id = 1 OR c.id = 2")]
    [InlineData("WHERE c.id NOT IN (1, 2)")]
    [InlineData("WHERE c.id <> 1")]
    [InlineData("WHERE c.id > 1")]
    [InlineData("WHERE NOT (c.id = 1)")]
    [InlineData("WHERE c.id IN (1, o.id)")]
    [InlineData("WHERE c.id = NULL")]
    [InlineData("WHERE c.id = 1 + 1")]
    [InlineData("WHERE UPPER(c.name) = 'A'")]
    public void NonConjunctiveOrNonEqualityPredicates_AreNotExtracted(string where)
    {
        Extract("SELECT c.id" + From + where).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("WITH x AS (SELECT id FROM erp.public.orders) SELECT c.id FROM crm_api.public.customers c JOIN x ON x.id = c.id WHERE c.id = 1")]
    [InlineData("SELECT c.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id WHERE c.id = 1 UNION SELECT id FROM erp.public.orders")]
    [InlineData("SELECT c.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id WHERE c.id = 1 AND o.id IN (SELECT id FROM erp.public.orders)")]
    [InlineData("SELECT c.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id WHERE c.id = 1 AND EXISTS (SELECT 1 FROM erp.public.orders)")]
    [InlineData("SELECT c.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = (SELECT MAX(id) FROM erp.public.orders) WHERE c.id = 1")]
    [InlineData("SELECT x.id FROM (SELECT id FROM crm_api.public.customers) x JOIN erp.public.orders o ON o.id = x.id WHERE x.id = 1")]
    [InlineData("SELECT a.id FROM crm_api.public.customers a JOIN crm_api.public.customers b ON a.id = b.id JOIN erp.public.orders o ON o.id = a.id WHERE a.id = 1")]
    public void IneligibleQueryShapes_YieldNoLookup(string sql)
    {
        Extract(sql).ShouldBeNull();
    }

    [Fact]
    public void WithoutAStatement_ThereIsNoLookup()
    {
        VirtualFilterPointLookupExtractor.Extract(null, [], Crm.Identifier).ShouldBeNull();
    }
}
