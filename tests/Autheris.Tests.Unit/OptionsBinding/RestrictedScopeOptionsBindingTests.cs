namespace Autheris.Tests.Unit.OptionsBinding;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Policy;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #143 (D-1/D-2/D-15): binding, defaults and startup validation of <c>Gateway:Governance:RestrictedScopes</c>.</summary>
public sealed class RestrictedScopeOptionsBindingTests
{
    private static RestrictedScopeOptions Bind(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return config.GetSection(RestrictedScopeOptions.SectionName).Get<RestrictedScopeOptions>() ?? new RestrictedScopeOptions();
    }

    private static string Key(string suffix) => $"{RestrictedScopeOptions.SectionName}:{suffix}";

    private sealed class ListLogger : ILogger<RestrictedScopeOptionsValidator>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static (ValidateOptionsResult Result, ListLogger Log) Validate(RestrictedScopeOptions options, string environment = "Production")
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        var log = new ListLogger();
        return (new RestrictedScopeOptionsValidator(env, log).Validate(null, options), log);
    }

    [Fact]
    public void SectionName_IsUnderGatewayGovernance() => RestrictedScopeOptions.SectionName.ShouldBe("Gateway:Governance:RestrictedScopes");

    [Fact]
    public void NothingConfigured_UsesDefaultList_EnabledTrue()
    {
        var o = Bind([]);

        o.Enabled.ShouldBeTrue();
        RestrictedScopePolicy.FromOptions(o).Scopes.ShouldBe(["Agent.Read"]);
    }

    [Fact]
    public void FullFormScope_MatchesBareTokenScope()
    {
        var policy = RestrictedScopePolicy.FromOptions(Bind(new() { [Key("Scopes:0")] = "api://app/Agent.Read" }));

        policy.HeldBy(new HashSet<string> { "Agent.Read" }).ShouldBe(["Agent.Read"]);
    }

    [Fact]
    public void EmptyArray_KeepsDefault_AndIsValid()
    {
        var o = Bind(new() { [Key("Scopes")] = null });

        RestrictedScopePolicy.FromOptions(o).Scopes.ShouldBe(["Agent.Read"]);
        Validate(o).Result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void EnabledFalse_DisablesPolicy_InDevelopment()
    {
        var o = Bind(new() { [Key("Enabled")] = "false" });

        Validate(o, "Development").Result.Succeeded.ShouldBeTrue();
        RestrictedScopePolicy.FromOptions(o).Scopes.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void T_GOV143_10_DisabledOutsideDevelopment_FailsStartup(string environment)
    {
        var (result, _) = Validate(new RestrictedScopeOptions { Enabled = false }, environment);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Enabled");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Agent Read")]
    [InlineData("api://app/")]
    public void T_GOV143_10_InvalidEntry_FailsStartup(string? entry)
    {
        var (result, _) = Validate(new RestrictedScopeOptions { Scopes = ["Agent.Read", entry!] });

        result.Failed.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_10_OversizedEntryAndList_Fail()
    {
        Validate(new RestrictedScopeOptions { Scopes = [new string('a', 257)] }).Result.Failed.ShouldBeTrue();
        Validate(new RestrictedScopeOptions { Scopes = [new string('a', 256)] }).Result.Succeeded.ShouldBeTrue();
        Validate(new RestrictedScopeOptions { Scopes = [.. Enumerable.Range(0, 65).Select(i => "s" + i)] }).Result.Failed.ShouldBeTrue();
        Validate(new RestrictedScopeOptions { Scopes = [.. Enumerable.Range(0, 64).Select(i => "s" + i)] }).Result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_10_ListWithoutAgentRead_Warns_DuplicatesCollapse()
    {
        var (result, log) = Validate(new RestrictedScopeOptions { Scopes = ["Data.Restricted", "api://x/Data.Restricted", "data.restricted"] });

        result.Succeeded.ShouldBeTrue();
        log.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("Data.Restricted"));
        RestrictedScopePolicy.FromOptions(new RestrictedScopeOptions { Scopes = ["Data.Restricted", "data.restricted"] }).Scopes.Count.ShouldBe(1);

        var (_, withAgent) = Validate(new RestrictedScopeOptions { Scopes = ["api://x/Agent.Read"] });
        withAgent.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning);
    }
}
