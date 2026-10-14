using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>WP-X0l: Casbin row filters are attached to the decision as typed policies (the text stays for the direct consumers).</summary>
public sealed class TypedCasbinRowPolicyTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly Sid User = new("S-1-5-21-U");
    private static readonly TableIdentifier Table = new("erp", "main", "invoices");

    private static RowPolicyFactory Factory() => new(new PolicyPredicateRenderer(), new PolicyExpressionParser(new FastSqlEngine()));

    private static CasbinEnforcementService Service(RowPolicyFactory? factory, params CasbinRuleInput[] rules)
    {
        var service = new CasbinEnforcementService(rowPolicies: factory);
        service.AddPolicies(Tenant, rules);
        return service;
    }

    private static CasbinRuleInput Rule(string? rls = null, ConsentRowFilter? correlated = null) =>
        new(User.Value, Table.ToString(), "read", RlsFilter: rls, CorrelatedRowFilter: correlated);

    private static SecurityEvaluationContext Context(IReadOnlyDictionary<string, object?>? attributes = null, DatabaseDialect? dialect = DatabaseDialect.Sqlite) =>
        new(User, [], Tenant, Table, ["id"], IPAddress.Loopback, DateTimeOffset.UtcNow, null,
            attributes ?? new Dictionary<string, object?> { ["action"] = "read" }, dialect)
        {
            TableColumns = ["id", "department", "region", "customer_id"]
        };

    private static ConsentRowFilter Correlated(string country) => new()
    {
        FilterType = RowFilterType.SubqueryCorrelated,
        DependentTable = new TableIdentifier("erp", "main", "customers"),
        DependentTableAlias = "c",
        ForeignKeyColumn = "customer_id",
        PrimaryKeyColumn = "id",
        SubqueryFilterPredicateJson = $"{{\"c.country\": \"{country}\"}}"
    };

    [Fact]
    public async Task RlsFilterText_IsTyped_AndTheDirectTextStaysUnchanged()
    {
        using var service = Service(Factory(), Rule("department = 'IT'"));

        var decision = await service.EvaluatePolicyAsync(Context());

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("department = 'IT'");
        decision.RowPolicy.ShouldNotBeNull();
        var projection = decision.RowPolicy!.Project();
        projection.Sql.ShouldNotContain("IT");
        projection.Parameters.Values.ShouldContain("IT");
    }

    [Fact]
    public async Task CorrelatedFilter_IsTyped_WithPerRuleParameterNames()
    {
        using var service = Service(Factory(), Rule(correlated: Correlated("CH")));

        var decision = await service.EvaluatePolicyAsync(Context());

        decision.RowPolicy.ShouldNotBeNull();
        var projection = decision.RowPolicy!.Project();
        projection.Sql.ShouldContain("EXISTS");
        projection.Parameters.Keys.ShouldAllBe(k => k.StartsWith("@__gql_rls_c0_"));
        projection.Parameters.Values.ShouldContain("CH");
    }

    [Fact]
    public async Task SeveralMatchedRules_AreOrCombinedLikeTheText()
    {
        using var service = Service(Factory(), Rule("department = 'IT'"), Rule("department = 'HR'"));

        var decision = await service.EvaluatePolicyAsync(Context());

        decision.CombinedRowFilterSql.ShouldBe("(department = 'IT') OR (department = 'HR')");
        var projection = decision.RowPolicy!.Project();
        projection.Sql.ShouldContain(" OR ");
        projection.Parameters.Values.OrderBy(v => v).ShouldBe(new object?[] { "HR", "IT" });
    }

    [Fact]
    public async Task ClaimPlaceholder_BecomesABoundParameter_NotText()
    {
        using var service = Service(Factory(), Rule("department = '${department}'"));

        var decision = await service.EvaluatePolicyAsync(Context(new Dictionary<string, object?> { ["action"] = "read", ["department"] = "Sales" }));

        decision.RowPolicy.ShouldNotBeNull();
        var projection = decision.RowPolicy!.Project();
        projection.Sql.ShouldNotContain("Sales");
        projection.Parameters.Values.ShouldContain("Sales");
    }

    [Fact]
    public async Task WithoutARowPolicyFactory_TheDecisionIsStringOnly()
    {
        using var service = Service(null, Rule("department = 'IT'"));

        var decision = await service.EvaluatePolicyAsync(Context());

        decision.RowPolicy.ShouldBeNull();
        decision.CombinedRowFilterSql.ShouldBe("department = 'IT'");
    }

    [Fact]
    public async Task TextThatIsNotCanonicalSql_StaysStringOnly_AndTheDecisionIsStillAllowed()
    {
        using var service = Service(Factory(), Rule("my_udf(department) = 'IT'"));

        var decision = await service.EvaluatePolicyAsync(Context());

        decision.IsAllowed.ShouldBeTrue();
        decision.RowPolicy.ShouldBeNull();
        decision.CombinedRowFilterSql.ShouldBe("my_udf(department) = 'IT'");
    }

    [Fact]
    public async Task FilterOnAColumnOutsideTheCatalog_StaysStringOnly()
    {
        using var service = Service(Factory(), Rule("secret_col = 'x'"));

        var decision = await service.EvaluatePolicyAsync(Context());

        decision.RowPolicy.ShouldBeNull();
        decision.CombinedRowFilterSql.ShouldBe("secret_col = 'x'");
    }

    [Fact]
    public async Task RuleWithoutAFilter_HasNoRowPolicy()
    {
        using var service = Service(Factory(), Rule());

        var decision = await service.EvaluatePolicyAsync(Context());

        decision.IsAllowed.ShouldBeTrue();
        decision.RowPolicy.ShouldBeNull();
        decision.CombinedRowFilterSql.ShouldBeNull();
    }

    [Fact]
    public async Task Restrict_MergesATypedCasbinPolicy_IntoTheTypedConsentPolicy()
    {
        using var service = Service(Factory(), Rule("department = 'IT'"));
        var casbin = await service.EvaluatePolicyAsync(Context());
        var consentPolicy = Factory().FromConsentFilters(
            new ConsentRowFilterRecipe([[new ConsentRowFilter { ColumnName = "region", ValueJson = "\"EU\"" }]], []), DatabaseDialect.Sqlite)!;
        var consent = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            .WithAdditionalRowPolicy(consentPolicy);
        var meta = new TableMetadata { Identifier = Table, Table = new Table { SourceType = "SQLite", DataSourceType = DataSourceType.Sql } };

        var merged = TableAccessPolicy.Restrict(consent, casbin, meta);

        merged.IsAllowed.ShouldBeTrue();
        merged.CombinedRowFilterSql.ShouldBe(merged.RowPolicy!.Project().Sql);
        merged.RowFilterParameters!.Values.OrderBy(v => v).ShouldBe(new object?[] { "EU", "IT" });
        merged.RequireTypedRowPolicy().ShouldBeSameAs(merged);
    }

    [Fact]
    public async Task Restrict_ForANonSqlSource_KeepsTheLegacyText()
    {
        using var service = Service(Factory(), Rule("department = 'IT'"));
        var casbin = await service.EvaluatePolicyAsync(Context());
        var consent = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), "region = 'EU'", hasUnconstrainedColumnAllow: true);
        var meta = new TableMetadata
        {
            Identifier = Table,
            Table = new Table { SourceType = "HttpDeclarative", DataSourceType = DataSourceType.HttpDeclarative }
        };

        var merged = TableAccessPolicy.Restrict(consent, casbin, meta);

        merged.RowPolicy.ShouldBeNull();
        merged.CombinedRowFilterSql.ShouldBe("(region = 'EU') AND (department = 'IT')");
    }
}
