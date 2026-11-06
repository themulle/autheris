namespace Autheris.Tests.Unit.DataSources;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Configuration.Validation;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Tests.Unit.TestSupport;
using Autheris.Application.DataSources;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #447 WP-5 (REQ-ATR-09, REQ-ATR-11, decisions 11 and 12, SEC-ATR-09): startup/reload warnings and admin diagnostics.</summary>
[Trait("Category", "Security")]
public sealed class DataSourceTransportConfigurationReporterTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        public List<string> Warnings => Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message).ToList();
    }

    private sealed class ReloadableMonitor<T>(T initial) : IOptionsMonitor<T>
    {
        private T _value = initial;
        private readonly List<Action<T, string?>> _listeners = [];

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return null;
        }

        public void Reload(T next)
        {
            _value = next;
            foreach (var listener in _listeners)
            {
                listener(next, null);
            }
        }
    }

    private static DataSourceConfigurationOptions Config(DataSourceTransportFlags? defaults = null, params (string Name, DataSourceTransportFlags? Flags)[] profiles)
    {
        var options = new DataSourceConfigurationOptions { Defaults = new DataSourceProfileOptions { AllowedTransports = defaults } };
        foreach (var (name, flags) in profiles)
        {
            options.Profiles[name] = new DataSourceProfileOptions { AllowedTransports = flags };
        }

        return options;
    }

    private static DataSourceTransportConfigurationReporter Reporter(
        IOptionsMonitor<DataSourceConfigurationOptions> dataSources, IOptionsMonitor<GatewayOptions> gateway,
        ILogger<DataSourceTransportConfigurationReporter> logger, IServiceScopeFactory? scopes = null) =>
        new(dataSources, () => gateway.CurrentValue, listener => gateway.OnChange((options, _) => listener(options)), logger, scopes);

    [Fact]
    public void Reporter_WarnsWhenNone()
    {
        var findings = DataSourceTransportConfigurationReporter.Evaluate(
            Config(null, ("locked", DataSourceTransportFlags.None), ("open", DataSourceTransportFlags.GraphQL)), new GatewayOptions());

        findings.ShouldHaveSingleItem().ShouldBe(new TransportConfigurationFinding("locked", TransportConfigurationFindingKind.NoTransportAllowed, DataSourceTransportFlags.None));
    }

    [Fact]
    public void Reporter_WarnsWhenDefaultsNone_AndProfilesInheritIt()
    {
        var findings = DataSourceTransportConfigurationReporter.Evaluate(
            Config(DataSourceTransportFlags.None, ("inheriting", null), ("explicit", DataSourceTransportFlags.Rest)), new GatewayOptions());

        findings.Select(f => f.Source).ShouldBe([DataSourceTransportConfigurationReporter.DefaultsName, "inheriting"], ignoreOrder: true);
    }

    [Fact]
    public void Reporter_WarnsWhenOnlyGloballyDisabledTransport()
    {
        var gateway = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = false }, Mcp = new McpOptions { Enabled = false } };
        var findings = DataSourceTransportConfigurationReporter.Evaluate(
            Config(null, ("sql_only", DataSourceTransportFlags.WebSql), ("sql_and_graphql", DataSourceTransportFlags.WebSql | DataSourceTransportFlags.GraphQL),
                ("mcp_only", DataSourceTransportFlags.Mcp)), gateway);

        findings.Select(f => f.Source).ShouldBe(["mcp_only", "sql_only"], ignoreOrder: true);
        findings.ShouldAllBe(f => f.Kind == TransportConfigurationFindingKind.OnlyGloballyDisabledTransports);
    }

    [Fact]
    public void Reporter_DefaultAll_NoWarning()
    {
        DataSourceTransportConfigurationReporter.Evaluate(Config(), new GatewayOptions()).ShouldBeEmpty();
        DataSourceTransportConfigurationReporter.Evaluate(Config(null, ("a", DataSourceTransportFlags.All), ("b", null)), new GatewayOptions()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Reporter_WarnsOnStart_AndOnReload()
    {
        var logger = new CapturingLogger<DataSourceTransportConfigurationReporter>();
        var dataSources = new ReloadableMonitor<DataSourceConfigurationOptions>(Config());
        var gateway = new ReloadableMonitor<GatewayOptions>(new GatewayOptions());
        using var reporter = Reporter(dataSources, gateway, logger);

        await reporter.StartAsync(CancellationToken.None);
        logger.Warnings.ShouldBeEmpty();

        dataSources.Reload(Config(null, ("locked", DataSourceTransportFlags.None)));
        logger.Warnings.ShouldHaveSingleItem().ShouldContain("locked");

        gateway.Reload(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = false } });
        logger.Warnings.Count.ShouldBeGreaterThan(1);
    }

    private static TableMetadata WithSource(TableMetadata row, string source) => row with { Table = row.Table with { SourceName = source } };

    [Fact]
    public async Task Reporter_ListsCatalogSourcesWithoutProfile_Sanitised()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(
        [
            Wp0.Table("profiled", "a"),
            Wp0.Table("plain", "b"),
            WithSource(Wp0.Table("x", "c"), "evil\nname")
        ]));
        var services = new ServiceCollection().AddSingleton(repo).BuildServiceProvider();
        var logger = new CapturingLogger<DataSourceTransportConfigurationReporter>();
        using var reporter = Reporter(
            new ReloadableMonitor<DataSourceConfigurationOptions>(Config(DataSourceTransportFlags.GraphQL, ("profiled", DataSourceTransportFlags.All))),
            new ReloadableMonitor<GatewayOptions>(new GatewayOptions()), logger, services.GetRequiredService<IServiceScopeFactory>());

        await reporter.StartAsync(CancellationToken.None);

        var message = logger.Warnings.ShouldHaveSingleItem();
        message.ShouldContain("plain");
        message.ShouldContain("evil_name");
        message.ShouldNotContain("profiled,");
        message.ShouldNotContain("\n");
    }

    [Fact]
    public async Task Reporter_CatalogUnreadable_DoesNotFailStart()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<TableMetadata>>>(_ => throw new InvalidOperationException("db down"));
        var services = new ServiceCollection().AddSingleton(repo).BuildServiceProvider();
        using var reporter = Reporter(
            new ReloadableMonitor<DataSourceConfigurationOptions>(Config()), new ReloadableMonitor<GatewayOptions>(new GatewayOptions()),
            new CapturingLogger<DataSourceTransportConfigurationReporter>(), services.GetRequiredService<IServiceScopeFactory>());

        await Should.NotThrowAsync(() => reporter.StartAsync(CancellationToken.None));
    }

    // ----- AdminDiagnostics_ShowsEffectiveTransports (decision 12: the existing admin data source listing carries the flag names) -----

    [Fact]
    public async Task AdminDiagnostics_ShowsEffectiveTransports()
    {
        using var resolver = new HierarchicalDataSourceOptionsResolver(
            new DataSourceTransportGateTests.FixedMonitor<DataSourceConfigurationOptions>(
                Config(DataSourceTransportFlags.WebSql | DataSourceTransportFlags.Rest, ("restricted", DataSourceTransportFlags.GraphQL), ("inheriting", null))));
        var tables = Wp0.Tables(Wp0.Table("restricted", "a"), Wp0.Table("inheriting", "b"), Wp0.Table("noprofile", "c"));
        var options = Wp0.Options(TenancyShape.Multi);
        var visibility = CatalogVisibilityTestFactory.Create(tables, Wp0.Consents(), options);
        var admin = Wp0.User("root", null, ["ClusterAdmin"]);
        var service = new CatalogDiscoveryService(
            tables, Substitute.For<IAuditLogRepository>(), Options.Create(options), visibility, transportResolver: resolver);

        var sources = await service.ListDatasourcesAsync(Wp0.Context(admin));

        sources.Count.ShouldBe(3);
        sources.Single(s => s.Domain == "restricted").AllowedTransports.ShouldBe(["GraphQL"]);
        sources.Single(s => s.Domain == "inheriting").AllowedTransports.ShouldBe(["WebSql", "Rest"], ignoreOrder: true);
        sources.Single(s => s.Domain == "noprofile").AllowedTransports.ShouldBe(["WebSql", "Rest"], ignoreOrder: true);
    }

    [Fact]
    public void TransportFlagNames_ListsSingleFlagsOnly()
    {
        TransportFlagNames.Of(DataSourceTransportFlags.None).ShouldBeEmpty();
        TransportFlagNames.Of(DataSourceTransportFlags.All).Count.ShouldBe(7);
        TransportFlagNames.Of(DataSourceTransportFlags.All).ShouldNotContain("All");
    }
}
