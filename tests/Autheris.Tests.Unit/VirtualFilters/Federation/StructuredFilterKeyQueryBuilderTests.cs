namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// F-GOV-14 WI-5 / WI-19 (M4, SEC-4, SEC-5, SEC-6): the key query runs the complete structured definition (FROM, every
/// JOIN, every condition) in the key source, with bound parameters for every value, a tenant predicate per tenanted
/// table and a row limit of <c>MaxAuthorizedKeys + 1</c> so an overflow is detected, never truncated.
/// </summary>
public sealed class StructuredFilterKeyQueryBuilderTests
{
    private static readonly IReadOnlyDictionary<string, string> NoTenantColumns = new Dictionary<string, string>();

    private static VirtualFilter David() => VirtualFilterModelTests.DavidFilter();

    private static VirtualFilter AllOperators() => new()
    {
        TenantId = new TenantId("tenant_a"),
        Name = "all_ops",
        Source = "erp",
        KeyColumns = ["a.client_id", "a.region"],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("erp", "auth", "assignment"),
            FromAlias = "a",
            Joins = [new FilterJoin(new TableIdentifier("erp", "md", "client"), "c", "c.id", "a.client_id")],
            Where =
            [
                new FilterCondition("a.owner", FilterConditionOperator.Eq, "O'Brien"),
                new FilterCondition("a.status", FilterConditionOperator.NotEq, "5"),
                new FilterCondition("c.weight", FilterConditionOperator.Eq, "1.50"),
                new FilterCondition("a.revoked_at", FilterConditionOperator.IsNull),
                new FilterCondition("c.name", FilterConditionOperator.IsNotNull)
            ]
        }
    };

    [Fact]
    public void Sqlite_DavidCase_SelectsDistinctKeysWithJoinConditionAndLimit()
    {
        var query = StructuredFilterKeyQueryBuilder.Build(David(), DatabaseDialect.Sqlite, NoTenantColumns, "tenant_lwe", maxRows: 11);

        query.Sql.ShouldBe(
            "SELECT DISTINCT \"client\".\"client_id\" AS \"client_id\" FROM \"client\" AS \"client\" " +
            "INNER JOIN \"crane\" AS \"crane\" ON \"crane\".\"serial_number\" = \"client\".\"crane_serial_number\" " +
            "WHERE \"crane\".\"is_delivered\" IS NULL AND \"client\".\"client_id\" IS NOT NULL LIMIT 11");
        query.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void SqlServer_DavidCase_UsesTopAndBrackets()
    {
        var query = StructuredFilterKeyQueryBuilder.Build(David(), DatabaseDialect.SqlServer, NoTenantColumns, "tenant_lwe", maxRows: 50_001);

        query.Sql.ShouldBe(
            "SELECT DISTINCT TOP (50001) [client].[client_id] AS [client_id] FROM [conf].[client] AS [client] " +
            "INNER JOIN [md].[crane] AS [crane] ON [crane].[serial_number] = [client].[crane_serial_number] " +
            "WHERE [crane].[is_delivered] IS NULL AND [client].[client_id] IS NOT NULL");
    }

    [Fact]
    public void PostgreSql_AllOperators_BoundParametersTenantPredicatesAndNotNullPerKey()
    {
        var tenantColumns = new Dictionary<string, string> { ["a"] = "tenant_id", ["c"] = "TenantId" };

        var query = StructuredFilterKeyQueryBuilder.Build(AllOperators(), DatabaseDialect.PostgreSql, tenantColumns, "tenant_a", maxRows: 101);

        query.Sql.ShouldBe(
            "SELECT DISTINCT \"a\".\"client_id\" AS \"client_id\", \"a\".\"region\" AS \"region\" " +
            "FROM \"auth\".\"assignment\" AS \"a\" INNER JOIN \"md\".\"client\" AS \"c\" ON \"c\".\"id\" = \"a\".\"client_id\" " +
            "WHERE \"a\".\"owner\" = @vf_p0 AND \"a\".\"status\" <> @vf_p1 AND \"c\".\"weight\" = @vf_p2 " +
            "AND \"a\".\"revoked_at\" IS NULL AND \"c\".\"name\" IS NOT NULL " +
            "AND \"a\".\"tenant_id\" = @vf_tenant AND \"c\".\"TenantId\" = @vf_tenant " +
            "AND \"a\".\"client_id\" IS NOT NULL AND \"a\".\"region\" IS NOT NULL LIMIT 101");

        var parameters = query.Parameters.ToDictionary(p => p.Key, p => p.Value);
        parameters["@vf_p0"].ShouldBe("O'Brien");
        parameters["@vf_p1"].ShouldBe(5L);
        parameters["@vf_p2"].ShouldBe(1.50m);
        parameters["@vf_tenant"].ShouldBe("tenant_a");
    }

    [Fact]
    public void ValuesAndTenantAreNeverPartOfTheSqlText()
    {
        // ST-15: the tenant is a parameter placeholder, never a literal; values cannot inject SQL.
        var filter = AllOperators() with
        {
            Structured = AllOperators().Structured! with { Where = [new FilterCondition("a.owner", FilterConditionOperator.Eq, "x' OR '1'='1")] }
        };

        foreach (var dialect in new[] { DatabaseDialect.Sqlite, DatabaseDialect.SqlServer, DatabaseDialect.PostgreSql })
        {
            var query = StructuredFilterKeyQueryBuilder.Build(filter, dialect, new Dictionary<string, string> { ["a"] = "tenant_id" }, "tenant_secret_value", 10);

            query.Sql.ShouldNotContain("tenant_secret_value");
            query.Sql.ShouldNotContain("OR '1'");
            query.Sql.ShouldContain("= @vf_tenant");
        }
    }

    [Fact]
    public void UnsupportedDialect_Throws()
    {
        Should.Throw<System.NotSupportedException>(() =>
            StructuredFilterKeyQueryBuilder.Build(David(), DatabaseDialect.Oracle, NoTenantColumns, "t", 10));
    }
}
