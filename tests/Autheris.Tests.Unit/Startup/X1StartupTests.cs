namespace Autheris.Tests.Unit.Startup;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy.Lint;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Databricks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-X1: SEC-ADG-28 (the removed engine switch fails the start) and SEC-ADG-26 (the start lists the denied tables).</summary>
public sealed class X1StartupTests
{
    private const string SecretValue = "SECRET_VALUE_42";

    private static IHostEnvironment Env(string name = "Development")
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    // ---- SEC-ADG-28 -------------------------------------------------------------------------------------------------

    private static IConfiguration ConfigWith(string? value)
    {
        var values = new Dictionary<string, string?>();
        if (value is not null) values["Gateway:WebSql:SqlRewriterEngine"] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData("AstCompiler")]
    [InlineData("LegacyTokenStream")]
    [InlineData("ShadowDualRun")]
    [InlineData("")]
    public void StartupFails_WhenSqlRewriterEngineConfigured(string value)
    {
        // The key is a removed kill switch: warn-and-ignore would let an operator believe the legacy path still exists.
        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateSqlRewriterEngine(ConfigWith(value)));

        ex.Message.ShouldContain("WebSql:SqlRewriterEngine");
        ex.Message.ShouldNotContain(value.Length > 0 ? value : "\0");   // the configured value is never echoed
    }

    [Fact]
    public void Startup_WithoutSqlRewriterEngine_PassesThatCheck()
    {
        Should.NotThrow(() => GatewayStartupValidator.ValidateSqlRewriterEngine(ConfigWith(null)));
        Should.NotThrow(() => GatewayStartupValidator.ValidateStartup(new GatewayOptions(), Env(), _ => null));
    }

    private static IConfiguration PlanCacheConfig(string key) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = "128" }).Build();

    [Theory]
    [InlineData("Development", "Gateway:WebSql:PlanCache:MaxEntries")]
    [InlineData("Production", "Gateway:WebSql:PlanCache:MaxEntries")]
    [InlineData("Production", "Gateway:WebSql:PlanCache")]
    public void StartupFails_WhenRemovedPlanCacheSectionConfigured_ThroughTheRealRegistration(string environment, string key)
    {
        // WebSql:PlanCache was removed with the legacy path (the engine template cache has a fixed size): ignoring it silently would
        // let an operator believe the cache size is tunable.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        var ex = Should.Throw<ValidationException>(() => Autheris.Api.Extensions.GatewayServiceCollectionExtensions.AddGatewayOptions(
            services, PlanCacheConfig(key), Env(environment)));

        ex.Message.ShouldContain("WebSql:PlanCache");
        ex.Message.ShouldNotContain("128");
    }

    [Fact]
    public void Startup_WithoutPlanCacheSection_PassesThatCheck() =>
        Should.NotThrow(() => GatewayStartupValidator.ValidateRemovedWebSqlSettings(ConfigWith(null)));

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void StartupFails_WhenSqlRewriterEngineConfigured_ThroughTheRealRegistration_InEveryEnvironment(string environment)
    {
        // AddGatewayOptions is the production entry point: it reads the configuration key before binding the options.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        Should.Throw<ValidationException>(() => Autheris.Api.Extensions.GatewayServiceCollectionExtensions.AddGatewayOptions(
            services, ConfigWith("AstCompiler"), Env(environment)));
    }

    // ---- L-6: the inert governance-bypass switch is reported once at startup, never per statement ------------------------------

    private static GatewayOptions WithWebSqlBypass() =>
        new() { Insecure = new InsecureGettingStartedOptions { danger_bypass_websql_governance = true } };

    [Fact]
    public void WebSqlGovernanceBypassFlag_FailsTheStart_OutsideDevelopment()
    {
        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateStartup(WithWebSqlBypass(), Env("Production"), _ => null));

        ex.Message.ShouldContain("danger_bypass_websql_governance");
    }

    [Fact]
    public void WebSqlGovernanceBypassFlag_InDevelopment_IsWarnedAboutOnceAtStartup_AsHavingNoEffect()
    {
        var logger = new CapturingLogger<GatewayOptions>();

        GatewayStartupValidator.ValidateStartup(WithWebSqlBypass(), Env("Development"), _ => null, logger);

        logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Text.Contains("danger_bypass_websql_governance", StringComparison.Ordinal)).ShouldBe(1);
        logger.Entries.ShouldContain(e => e.Text.Contains("no effect", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WebSqlGovernanceBypassFlag_IsNotWarnedAboutPerStatement()
    {
        var logger = new CapturingLogger<Autheris.Application.Sql.Services.GovernedSqlRewriter>();
        var gateway = WithWebSqlBypass();
        gateway = new GatewayOptions { Insecure = gateway.Insecure, WebSql = new WebSqlOptions { Enabled = true } };
        var options = Microsoft.Extensions.Options.Options.Create(gateway);
        var rewriter = new Autheris.Application.Sql.Services.GovernedSqlRewriter(options, logger: logger);
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "S-1-5-21-L6"), new System.Security.Claims.Claim("tenant_id", "t")], "Test"));

        // the statement is refused later (no table repository); only the log of the bypass flag matters here
        await Should.ThrowAsync<Exception>(() => rewriter.ValidateAsync("SELECT 1 FROM orders", user, new TenantId("t")));

        logger.Lines.ShouldNotContain(l => l.Contains("danger_bypass_websql_governance", StringComparison.Ordinal));
    }

    // ---- SEC-ADG-26 -------------------------------------------------------------------------------------------------

    private static TableMetadata Orders(string sourceType = "PostgreSQL") => new()
    {
        Table = new Table { SourceType = sourceType, SourceName = "erp", SchemaName = "main", TableName = "orders" },
        Identifier = new TableIdentifier("erp", "main", "orders"),
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "region", DataType = "varchar" }, new TableColumn { ColumnName = "name", DataType = "varchar" }]
    };

    private static AccessProfile Profile(string id, string predicate) => new()
    {
        ProfileId = id, Name = id, TenantId = TenantId.LegacySingleTenant, TargetTables = ["erp.main.orders"], RowFilterPredicate = predicate
    };

    [Fact]
    public void Startup_ListsDeniedTables_IdsOnly()
    {
        var log = new CapturingLogger<X1StartupTests>();
        var input = new PolicyLintInput(
            [Orders(), Orders("Databricks") with { Table = new Table { SourceType = "Databricks", SourceName = "lake", SchemaName = "s", TableName = "events" }, Identifier = new TableIdentifier("lake", "s", "events") }],
            [Profile("p-good", "region = 'EU'"), Profile("p-bad", $"region = '{SecretValue}' AND [name] = '{SecretValue}'")],
            [], []);

        var report = GatewayStartupValidator.ListDeniedTables(input, log);

        // the unparsable predicate denies its table; the table of a dialect that WebSQL does not admit is denied as well
        report.ShouldContain(d => d.Table == "erp.main.orders" && d.PolicyId == "p-bad");
        report.ShouldContain(d => d.Table == "lake.s.events" && d.Code == GatewayStartupValidator.DeniedDialectNotAdmitted);
        report.ShouldNotContain(d => d.PolicyId == "p-good");

        var text = string.Join('\n', log.Lines);
        text.ShouldContain("erp.main.orders");
        text.ShouldContain("p-bad");
        text.ShouldContain("lake.s.events");
        text.ShouldNotContain(SecretValue);   // ids only: never predicate text, parameter values or column names of a predicate
        text.ShouldNotContain("region");
    }

    [Fact]
    public void Startup_ListsNothing_WhenEveryTableIsUsable()
    {
        var log = new CapturingLogger<X1StartupTests>();

        var report = GatewayStartupValidator.ListDeniedTables(new PolicyLintInput([Orders()], [Profile("p-good", "region = 'EU'")], [], []), log);

        report.ShouldBeEmpty();
        string.Join('\n', log.Lines).ShouldNotContain("denied");
    }

    [Fact]
    public async Task DeniedTablesReporter_ReadsTheStoreAndLogsIdsOnly_AndNeverBlocksTheStart()
    {
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([Orders()]));
        var profiles = Substitute.For<IAccessProfileRepository>();
        profiles.GetAllProfilesAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AccessProfile>>([Profile("p-bad", $"[name] = '{SecretValue}'")]));
        var log = new CapturingLogger<DeniedTablesStartupReporter>();
        var reporter = new DeniedTablesStartupReporter(tables, profiles, null, Microsoft.Extensions.Options.Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = "tenant-a" } }), log);

        await reporter.StartAsync(CancellationToken.None);
        await reporter.ExecuteTask!;   // the listing runs in the background

        string.Join('\n', log.Lines).ShouldContain("p-bad");
        string.Join('\n', log.Lines).ShouldNotContain(SecretValue);

        // a store that cannot be read is a warning, not a failed start
        tables.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<TableMetadata>>>(_ => throw new InvalidOperationException(SecretValue));
        var failing = new CapturingLogger<DeniedTablesStartupReporter>();
        var failingReporter = new DeniedTablesStartupReporter(tables, profiles, null, Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), failing);
        await failingReporter.StartAsync(CancellationToken.None);
        await failingReporter.ExecuteTask!;
        string.Join('\n', failing.Lines).ShouldNotContain(SecretValue);
    }

    [Fact]
    public async Task DeniedTablesReporter_DoesNotBlockTheStartWhileTheStoreIsRead()
    {
        // a slow governance store must not delay the host start (the listing is background work, not part of StartAsync)
        var release = new TaskCompletionSource<IReadOnlyList<TableMetadata>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(_ => release.Task);
        var reporter = new DeniedTablesStartupReporter(
            tables, Substitute.For<IAccessProfileRepository>(), null, Microsoft.Extensions.Options.Options.Create(new GatewayOptions()),
            new CapturingLogger<DeniedTablesStartupReporter>());

        var start = reporter.StartAsync(CancellationToken.None);

        (await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(2)))).ShouldBeSameAs(start, "StartAsync waited for the store");
        release.SetResult([Orders()]);
        await reporter.ExecuteTask!;
        await reporter.StopAsync(CancellationToken.None);
    }
}
