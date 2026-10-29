namespace Autheris.Tests.Unit.Olap;

using System;
using System.Linq;
using System.Reflection;
using Autheris.Api.Configuration.Validation;
using Autheris.Application.Federation;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>PLAN-SEC-OLAP-01 WP-SO-1: gate matrix, classifier, transport names and start-up validation.</summary>
public sealed class CrossSourceOptionsValidatorTests
{
    private static GatewayOptions WithCross(CrossSourceOptions cross) => new() { WebSql = new WebSqlOptions { CrossSource = cross } };

    private static ValidateOptionsResult Validate(GatewayOptions options) => new CrossSourceOptionsValidator().Validate(null, options);

    [Fact]
    public void Defaults_AreValid_AndOlapIsNotInTheDefaultTransports()
    {
        Validate(new GatewayOptions()).Succeeded.ShouldBeTrue();
        new CrossSourceOptions().GetEffectiveAllowedTransports().ShouldNotContain("Olap", StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Olpa")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" Olap")]
    [InlineData("Graphql2")]
    public void AllowedTransports_UnknownOrBlankName_FailsAndListsValidNames(string name)
    {
        var result = Validate(WithCross(new CrossSourceOptions { AllowedTransports = [name] }));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("WebSql");
        result.FailureMessage.ShouldContain("Olap");
    }

    [Fact]
    public void AllowedTransports_KnownNamesInAnyCase_NullAndEmpty_AreValid()
    {
        Validate(WithCross(new CrossSourceOptions { AllowedTransports = ["olap", "WEBSQL", "Trino", "Olap"] })).Succeeded.ShouldBeTrue();
        Validate(WithCross(new CrossSourceOptions { AllowedTransports = [] })).Succeeded.ShouldBeTrue();
        Validate(WithCross(new CrossSourceOptions { AllowedTransports = null })).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void NonPositiveLimits_FailValidation()
    {
        Validate(WithCross(new CrossSourceOptions { MaxTableCount = 0 })).Failed.ShouldBeTrue();
        Validate(WithCross(new CrossSourceOptions { MaxStagedRowsPerTable = -1 })).Failed.ShouldBeTrue();
        Validate(WithCross(new CrossSourceOptions { MaxTotalStagedRows = 0 })).Failed.ShouldBeTrue();
        Validate(WithCross(new CrossSourceOptions { MaxStagedBytesPerTable = 0 })).Failed.ShouldBeTrue();
        Validate(WithCross(new CrossSourceOptions { MaxTotalStagedBytes = 0 })).Failed.ShouldBeTrue();
        Validate(WithCross(new CrossSourceOptions { TimeoutSeconds = 0 })).Failed.ShouldBeTrue();
        Validate(WithCross(new CrossSourceOptions { MaxParallelSourceReads = 0 })).Failed.ShouldBeTrue();
        Validate(WithCross(new CrossSourceOptions { MaxParallelSourceReads = 17 })).Failed.ShouldBeTrue();
    }

    [Fact]
    public void DuckDbOlapOptions_InvalidValues_FailValidation()
    {
        Validate(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { MaxStagedRowsPerTable = 0 } }).Failed.ShouldBeTrue();
        Validate(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { MaxConcurrentQueries = 0 } }).Failed.ShouldBeTrue();
        Validate(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { MaxConcurrentQueriesPerTenant = 0 } }).Failed.ShouldBeTrue();
        Validate(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { MaxThreads = 9 } }).Failed.ShouldBeTrue();
        Validate(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { MaxMemory = "lots" } }).Failed.ShouldBeTrue();
        Validate(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { MaxTempDirectorySize = "1 bucket" } }).Failed.ShouldBeTrue();
        Validate(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { QueryTimeoutSeconds = 0 } }).Failed.ShouldBeTrue();
        Validate(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { MaxSqlLength = 0 } }).Failed.ShouldBeTrue();
        Validate(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { MaxStagedBytesPerTable = 0 } }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void CrossSourceOptions_NoLongerExposesMaxMemoryAndMaxTempDirectorySize()
    {
        var names = typeof(CrossSourceOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();

        names.ShouldNotContain("MaxMemory");
        names.ShouldNotContain("MaxTempDirectorySize");
    }

    // ---- gate ----

    private sealed class MutableOptions(GatewayOptions value) : IOptions<GatewayOptions>
    {
        public GatewayOptions Value { get; set; } = value;
    }

    [Theory]
    [InlineData(false, "Olap", "Olap", false)]
    [InlineData(true, "Olap", "Olap", true)]
    [InlineData(true, "Olap", "olap", true)]
    [InlineData(true, "WebSql", "Olap", false)]
    [InlineData(true, "Olap", null, false)]
    [InlineData(true, "Olap", "", false)]
    [InlineData(true, "Olap", "  ", false)]
    public void FederationGate_Matrix(bool enabled, string listed, string? transport, bool expected)
    {
        var gate = new FederationGate(Options.Create(WithCross(new CrossSourceOptions { Enabled = enabled, AllowedTransports = [listed] })));

        gate.IsEnabledFor(transport).ShouldBe(expected);
        if (expected)
        {
            Should.NotThrow(() => gate.EnsureAllowed(transport));
        }
        else
        {
            Should.Throw<Autheris.Application.Sql.WebSqlPolicyException>(() => gate.EnsureAllowed(transport))
                .Message.ShouldBe(FederationGate.DeniedMessage);
        }
    }

    [Fact]
    public void FederationGate_EmptyListDeniesEverything_AndDefaultListNeverAllowsOlap()
    {
        new FederationGate(Options.Create(WithCross(new CrossSourceOptions { Enabled = true, AllowedTransports = [] }))).IsEnabledFor("WebSql").ShouldBeFalse();
        var defaults = new FederationGate(Options.Create(WithCross(new CrossSourceOptions { Enabled = true })));
        defaults.IsEnabledFor("WebSql").ShouldBeTrue();
        defaults.IsEnabledFor("Olap").ShouldBeFalse();
    }

    [Fact]
    public void FederationGate_ReadsLiveOptions()
    {
        var holder = new MutableOptions(WithCross(new CrossSourceOptions { Enabled = false }));
        var gate = new FederationGate(holder);
        gate.IsEnabledFor("WebSql").ShouldBeFalse();

        holder.Value = WithCross(new CrossSourceOptions { Enabled = true });

        gate.IsEnabledFor("WebSql").ShouldBeTrue();
    }

    // ---- classifier ----

    private static ResolvedSource Src(string connector, string label, DataSourceType type = DataSourceType.Sql) => new(connector, label, type);

    [Fact]
    public void Classifier_Cases()
    {
        CrossSourceClassifier.IsCrossSource([Src("a", "x"), Src("a", "X")]).ShouldBeFalse();
        CrossSourceClassifier.IsCrossSource([Src("a", "x"), Src("A", "x")]).ShouldBeFalse();
        CrossSourceClassifier.IsCrossSource([Src("a", "x"), Src("b", "x")]).ShouldBeTrue();
        CrossSourceClassifier.IsCrossSource([Src("a", "x"), Src("a", "y")]).ShouldBeTrue();
        CrossSourceClassifier.IsCrossSource([Src("a", "x", DataSourceType.HttpDeclarative)]).ShouldBeTrue();
        CrossSourceClassifier.IsCrossSource([Src("a", "x")]).ShouldBeFalse();
        CrossSourceClassifier.IsCrossSource([]).ShouldBeFalse();
    }

    [Fact]
    public void ResolvedSource_UsesTheDomainWhenNoSourceNameIsSet()
    {
        var meta = new TableMetadata { Identifier = new Autheris.Domain.Common.TableIdentifier("sales", "public", "t") };

        ResolvedSource.From("c", meta).SourceLabel.ShouldBe("sales");
    }

    [Fact]
    public void TransportNames_IncludeOlap_AndAreUnique()
    {
        SqlTransports.All.ShouldContain(SqlTransports.Olap);
        SqlTransports.All.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(SqlTransports.All.Count);
    }

    // ---- BC-OLAP-18 warnings ----

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<CrossSourceOptionsValidator>
    {
        public System.Collections.Generic.List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static Microsoft.Extensions.Configuration.IConfiguration Config(params (string Key, string Value)[] values) =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new System.Collections.Generic.KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("MaxMemory")]
    [InlineData("MaxTempDirectorySize")]
    public void Removed_CrossSource_Keys_Fail_Validation(string key)
    {
        // L7: the removed keys no longer only warn; they fail validation, name the key and the replacement and never echo the value.
        var validator = new CrossSourceOptionsValidator(Config(($"Gateway:WebSql:CrossSource:{key}", "256MB-SECRET")), new CapturingLogger());

        var result = validator.Validate(null, new GatewayOptions());

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains($"Gateway:WebSql:CrossSource:{key}", StringComparison.Ordinal) && f.Contains("Gateway:DuckDbOlap", StringComparison.Ordinal));
        result.Failures.ShouldNotContain(f => f.Contains("256MB-SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public void OlapListedWhileFederationDisabled_LogsAWarning_AndNoWarningWhenEnabled()
    {
        var logger = new CapturingLogger();
        var validator = new CrossSourceOptionsValidator(Config(), logger);

        validator.Validate(null, WithCross(new CrossSourceOptions { Enabled = false, AllowedTransports = ["Olap"] }));
        logger.Entries.ShouldContain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("Olap", StringComparison.Ordinal));

        var quiet = new CapturingLogger();
        new CrossSourceOptionsValidator(Config(), quiet).Validate(null, WithCross(new CrossSourceOptions { Enabled = true, AllowedTransports = ["Olap"] }));
        quiet.Entries.ShouldBeEmpty();
    }
}
