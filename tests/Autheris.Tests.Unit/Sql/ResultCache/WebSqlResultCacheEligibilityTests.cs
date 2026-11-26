namespace Autheris.Tests.Unit.Sql.ResultCache;

using Autheris.Application.Sql.ResultCache;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

public sealed class WebSqlResultCacheEligibilityTests
{
    private static readonly TableIdentifier Orders = new("main", "dbo", "Orders");

    private static WebSqlResultCacheEligibilityInput Eligible() => new(
        GlobalMaxMegabytes: 64,
        SourceTtlSeconds: 300,
        EnforcementMode: EnforcementMode.AutherisEnforced,
        UsesPerUserCredentials: false,
        HasConnection: true,
        IsDml: false,
        IsCrossSource: false,
        IsSelectStatement: true,
        HasRecursiveCte: false,
        HasPageOffsetParameter: false,
        CompiledSql: "SELECT lower(a), count(b) FROM dbo.Orders WHERE tenant = @p0 GROUP BY lower(a)",
        Dialect: TargetSqlDialect.PostgreSql,
        FunctionCalls: ["lower", "count"],
        AccessedTables: [Orders],
        AllowedTables: [],
        UserSid: "S-1-5-21-1",
        PrincipalKey: "abc123");

    [Fact]
    public void Plain_select_is_eligible()
    {
        WebSqlResultCacheEligibility.Evaluate(Eligible()).ShouldBeNull();
    }

    [Fact]
    public void Global_switch_off_gives_source_disabled()
    {
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { GlobalMaxMegabytes = 0 }).ShouldBe(WebSqlResultCacheReasons.SourceDisabled);
    }

    [Fact]
    public void Source_ttl_zero_gives_source_disabled()
    {
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { SourceTtlSeconds = 0 }).ShouldBe(WebSqlResultCacheReasons.SourceDisabled);
    }

    [Fact]
    public void Missing_connection_is_ineligible()
    {
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { HasConnection = false }).ShouldBe(WebSqlResultCacheReasons.NoConnection);
    }

    [Fact]
    public void Dml_and_non_select_are_not_read_only()
    {
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { IsDml = true }).ShouldBe(WebSqlResultCacheReasons.NotReadOnly);
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { IsSelectStatement = false }).ShouldBe(WebSqlResultCacheReasons.NotReadOnly);
    }

    [Fact]
    public void Recursive_cte_is_ineligible()
    {
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { HasRecursiveCte = true }).ShouldBe(WebSqlResultCacheReasons.RecursiveCte);
    }

    [Fact]
    public void Page_offset_parameter_is_ineligible()
    {
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { HasPageOffsetParameter = true }).ShouldBe(WebSqlResultCacheReasons.PagingUnavailable);
    }

    [Fact]
    public void Caller_without_sid_or_principal_key_is_not_bindable()
    {
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { UserSid = null }).ShouldBe(WebSqlResultCacheReasons.CallerNotBindable);
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { PrincipalKey = null }).ShouldBe(WebSqlResultCacheReasons.CallerNotBindable);
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { UserSid = " " }).ShouldBe(WebSqlResultCacheReasons.CallerNotBindable);
    }

    [Fact]
    public void Table_allowlist_is_case_insensitive_and_requires_every_table()
    {
        var other = new TableIdentifier("main", "dbo", "Customers");

        WebSqlResultCacheEligibility.Evaluate(Eligible() with { AllowedTables = ["DBO.ORDERS"] }).ShouldBeNull();
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { AllowedTables = ["dbo.Customers"] }).ShouldBe(WebSqlResultCacheReasons.TableNotListed);
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { AllowedTables = ["dbo.Orders"], AccessedTables = [Orders, other] })
            .ShouldBe(WebSqlResultCacheReasons.TableNotListed);
    }

    [Fact]
    public void Empty_allowlist_means_all_tables()
    {
        var other = new TableIdentifier("main", "dbo", "Customers");

        WebSqlResultCacheEligibility.Evaluate(Eligible() with { AllowedTables = null, AccessedTables = [Orders, other] }).ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(EnforcementMode.Delegated)]
    [InlineData(EnforcementMode.DelegatedDefenceInDepth)]
    public void Delegated_and_obo_ineligible_SECRC09(EnforcementMode mode)
    {
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { EnforcementMode = mode }).ShouldBe(WebSqlResultCacheReasons.DelegatedSource);
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { UsesPerUserCredentials = true }).ShouldBe(WebSqlResultCacheReasons.PerUserCredentials);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Cross_source_ineligible_SECRC09()
    {
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { IsCrossSource = true }).ShouldBe(WebSqlResultCacheReasons.CrossSource);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("SELECT a FROM dbo.Orders FOR UPDATE")]
    [InlineData("SELECT a INTO dbo.copy FROM dbo.Orders")]
    [InlineData("SELECT a FROM dbo.Orders TABLESAMPLE SYSTEM (10)")]
    [InlineData("SELECT a FROM dbo.Orders WITH (UPDLOCK)")]
    [InlineData("SELECT a FROM dbo.Orders FOR SHARE")]
    public void For_update_into_tablesample_ineligible_SECRC09(string sql)
    {
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { CompiledSql = sql }).ShouldBe(WebSqlResultCacheReasons.UnsupportedClause);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "now")]
    [InlineData(TargetSqlDialect.SqlServer, "getdate")]
    [InlineData(TargetSqlDialect.Oracle, "sysdate")]
    [InlineData(TargetSqlDialect.Databricks, "current_timestamp")]
    [InlineData(TargetSqlDialect.PostgreSql, "random")]
    public void Volatile_functions_ineligible_per_dialect_SECRC10(TargetSqlDialect dialect, string function)
    {
        var input = Eligible() with { Dialect = dialect, FunctionCalls = ["lower", function], CompiledSql = "SELECT lower(a) FROM dbo.Orders" };

        WebSqlResultCacheEligibility.Evaluate(input).ShouldBe(WebSqlResultCacheReasons.VolatileFunction);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Unknown_function_is_volatile_SECRC10()
    {
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { FunctionCalls = ["lower", "my_udf"] }).ShouldBe(WebSqlResultCacheReasons.VolatileFunction);
        WebSqlResultCacheEligibility.Evaluate(Eligible() with { FunctionCalls = null }).ShouldBe(WebSqlResultCacheReasons.VolatileFunction);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Policy_injected_volatile_in_compiled_sql_ineligible_SECRC10()
    {
        var input = Eligible() with
        {
            CompiledSql = "SELECT a FROM dbo.Orders WHERE owner = CURRENT_USER",
            FunctionCalls = ["lower"]
        };

        WebSqlResultCacheEligibility.Evaluate(input).ShouldBe(WebSqlResultCacheReasons.VolatileFunction);
    }
}
