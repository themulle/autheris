namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Hosting.Warmup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>PLAN-OPS-150 WP-A: opt-in cold-path timing listener (D-150 WP-A, SEC-OPS150-12).</summary>
public sealed class ColdPathTimingTests
{
    private sealed class CapturingLogger : ILogger<ColdPathTimingListener>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static IConfiguration Config(bool? enabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(enabled is null
                ? []
                : new Dictionary<string, string?> { ["Gateway:Startup:ColdPathTiming:Enabled"] = enabled.Value ? "true" : "false" })
            .Build();

    [Fact]
    public void ColdPathTimingOptions_DefaultDisabled_RegistersNoListener()
    {
        var services = new ServiceCollection();
        services.AddAutherisStartupWarmup(Config(null));

        services.Any(d => d.ServiceType == typeof(ColdPathTimingListener)).ShouldBeFalse();
        new ColdPathTimingOptions().Enabled.ShouldBeFalse();
    }

    [Fact]
    public void ColdPathTimingOptions_Enabled_RegistersListener()
    {
        var services = new ServiceCollection();
        services.AddAutherisStartupWarmup(Config(true));

        services.Any(d => d.ServiceType == typeof(ColdPathTimingListener)).ShouldBeTrue();
    }

    [Fact]
    public void ColdPathTimingListener_LogsStageNamesAndDurations_NoDocumentText()
    {
        var logger = new CapturingLogger();
        var listener = new ColdPathTimingListener(logger);

        listener.ParseDocument(null!).Dispose();
        listener.ValidateDocument(null!).Dispose();
        listener.CompileOperation(null!).Dispose();
        listener.ExecuteOperation(null!).Dispose();

        var text = string.Join('\n', logger.Messages);
        foreach (var stage in new[] { "parse", "validate", "compile", "execute" })
        {
            text.ShouldContain(stage);
        }

        logger.Messages.Count.ShouldBe(4);
        text.ShouldContain(" ms");
        text.ShouldNotContain("query");
        text.ShouldNotContain("{");
    }

    [Fact]
    public void ColdPathTimingListener_StopsAfterThreeOperations()
    {
        var logger = new CapturingLogger();
        var listener = new ColdPathTimingListener(logger);

        for (var i = 0; i < 6; i++)
        {
            listener.ParseDocument(null!).Dispose();
        }

        logger.Messages.Count.ShouldBe(3);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void ColdPathTimingOptions_EnabledOutsideDevelopment_RejectsStart(string environment)
    {
        // T-OPS150-15 / SEC-OPS150-12
        var validator = new ColdPathTimingOptionsValidator(Env(environment));

        var result = validator.Validate(null, new ColdPathTimingOptions { Enabled = true });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Development");
        validator.Validate(null, new ColdPathTimingOptions { Enabled = false }).Succeeded.ShouldBeTrue();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void ColdPathTimingOptions_EnabledInDevelopment_PassesValidation()
    {
        var validator = new ColdPathTimingOptionsValidator(Env("Development"));

        validator.Validate(null, new ColdPathTimingOptions { Enabled = true }).Succeeded.ShouldBeTrue();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void ColdPathTimingOptions_EnabledOutsideDevelopment_FailsOnStartValidation()
    {
        // T-OPS150-15: the options pipeline (ValidateOnStart) actually carries the validator.
        var services = new ServiceCollection();
        var config = Config(true);
        services.AddSingleton(config);
        services.AddSingleton(Env("Production"));
        services.AddAutherisStartupWarmup(config);

        using var sp = services.BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => sp.GetRequiredService<IOptions<ColdPathTimingOptions>>().Value);
    }
}
