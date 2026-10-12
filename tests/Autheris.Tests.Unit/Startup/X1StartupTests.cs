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

    [Theory]
    [InlineData("AstCompiler")]
    [InlineData("LegacyTokenStream")]
    [InlineData("ShadowDualRun")]
    [InlineData("")]
    public void StartupFails_WhenSqlRewriterEngineConfigured(string value)
    {
        // The key is a removed kill switch: warn-and-ignore would let an operator believe the legacy path still exists.
        var options = new GatewayOptions { WebSql = new WebSqlOptions { SqlRewriterEngine = value } };

        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateStartup(options, Env(), _ => null));

        ex.Message.ShouldContain("WebSql:SqlRewriterEngine");
        ex.Message.ShouldNotContain(value.Length > 0 ? value : "\0");   // the configured value is never echoed
    }

    [Fact]
    public void Startup_WithoutSqlRewriterEngine_PassesThatCheck()
    {
        Should.NotThrow(() => GatewayStartupValidator.ValidateStartup(new GatewayOptions(), Env(), _ => null));
    }

    [Fact]
    public void StartupFails_WhenSqlRewriterEngineConfigured_AlsoOutsideDevelopment()
    {
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateSqlRewriterEngine(
            new GatewayOptions { WebSql = new WebSqlOptions { SqlRewriterEngine = "AstCompiler" } }));
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
        var reporter = new DeniedTablesStartupReporter(tables, profiles, null, Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), log);

        await reporter.StartAsync(CancellationToken.None);

        string.Join('\n', log.Lines).ShouldContain("p-bad");
        string.Join('\n', log.Lines).ShouldNotContain(SecretValue);

        // a store that cannot be read is a warning, not a failed start
        tables.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<TableMetadata>>>(_ => throw new InvalidOperationException(SecretValue));
        var failing = new CapturingLogger<DeniedTablesStartupReporter>();
        await new DeniedTablesStartupReporter(tables, profiles, null, Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), failing).StartAsync(CancellationToken.None);
        string.Join('\n', failing.Lines).ShouldNotContain(SecretValue);
    }
}
