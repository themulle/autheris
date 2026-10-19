namespace Autheris.Tests.Unit.Sql.TimeZones;

using System;
using System.Linq;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Olap;
using Autheris.Application.Sql.TimeZones;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

/// <summary>T-29, ST-130-12 and the registration of REQ-SQL-130: closed lists stay closed, the operator form stays rejected, the state is wired once.</summary>
public sealed class ConvertTimeZoneSurfaceTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void T29_TheOlapAllowlistContainsNeitherTheFunctionNorTheDuckDbOne()
    {
        OlapSqlPolicy.AllowedFunctions.ShouldNotContain("convert_timezone");
        OlapSqlPolicy.AllowedFunctions.ShouldNotContain("timezone");
        OlapSqlPolicy.AllowedFunctions.ShouldNotContain("at_timezone");
    }

    [Theory]
    [InlineData("SELECT timezone('UTC', o.ts) FROM orders o")]
    [InlineData("SELECT convert_timezone('UTC', 'Europe/Berlin', o.ts) FROM orders o")]
    [Trait("Category", "Security")]
    public void T29_AnOlapCrossSourceStatementCallingTheFunctionsIsStillRejected(string sql)
    {
        var inspection = OlapSqlInspector.Inspect(sql);
        var ex = Should.Throw<OlapSqlRejectedException>(() => OlapSqlPolicy.EnforceCrossSource(inspection, ["orders"], allowNonEquiJoins: false));
        ex.Code.ShouldBe(OlapSqlRejectedException.ConstructNotAllowed);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_130_8_TheVirtualFilterFunctionListIsClosedAndDoesNotContainTheFunction()
    {
        SqlFilterCompiler.AllowedFunctions.ShouldNotContain("convert_timezone");
        SqlFilterCompiler.AllowedFunctions.ShouldNotContain("timezone");
        SqlFilterCompiler.AllowedFunctions.ShouldNotContain("at_timezone");
    }

    [Theory]
    [InlineData("SELECT o.ts AT TIME ZONE 'UTC' FROM orders o")]
    [InlineData("SELECT o.id FROM orders o WHERE o.ts AT TIME ZONE 'UTC' > TIMESTAMP '2026-01-01 00:00:00'")]
    [InlineData("SELECT o.id FROM orders o ORDER BY o.ts AT TIME ZONE 'UTC'")]
    [InlineData("SELECT CAST(o.ts AT TIME ZONE 'UTC' AS timestamp) FROM orders o")]
    [InlineData("WITH c AS (SELECT o.ts AT TIME ZONE 'UTC' AS t FROM orders o) SELECT t FROM c")]
    [InlineData("SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(o.ts AT TIME ZONE 'UTC' AS timestamp)) FROM orders o")]
    [Trait("Category", "Security")]
    public void ST_130_12_TheOperatorFormIsRejectedByTheBuilderInEveryPosition(string sql)
    {
        var engine = new FastSqlEngine();
        var (tree, _) = engine.Parse(sql.AsMemory());
        Should.Throw<AstBuildException>(() => new SqlAstBuilder().BuildStatement(tree));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_130_12_TheOperatorFormIsRejectedInAPolicyPredicate()
    {
        var services = new ServiceCollection();
        services.AddAutherisGovernance(new GatewayOptions());
        services.AddSingleton<ISqlEngine, FastSqlEngine>();
        var factory = services.BuildServiceProvider().GetRequiredService<Autheris.Application.Policy.RowPolicyFactory>();

        Should.Throw<Exception>(() => factory.ParseAdminPredicate(
            "created_at AT TIME ZONE 'UTC' > TIMESTAMP '2026-01-01 00:00:00'", new TrinoSqlEngine.Governance.TableIdentity("main", "events"),
            ["created_at"], Autheris.Domain.Common.DatabaseDialect.PostgreSql));
    }

    [Fact]
    public void TheStateIsRegisteredOnceAsASingletonWithItsStartupCheck()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutherisExecution(new GatewayOptions());

        services.Count(d => d.ServiceType == typeof(TimeZoneConversionState)).ShouldBe(1);
        services.Single(d => d.ServiceType == typeof(TimeZoneConversionState)).Lifetime.ShouldBe(ServiceLifetime.Singleton);
        services.Count(d => d.ServiceType == typeof(ITimeZoneConversionState)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(TimeZoneConversionStartupCheck)).ShouldBe(1);
    }
}
