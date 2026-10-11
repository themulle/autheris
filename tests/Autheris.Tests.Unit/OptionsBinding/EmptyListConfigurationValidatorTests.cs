namespace Autheris.Tests.Unit.OptionsBinding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;

/// <summary>
/// A JSON <c>[]</c> binds to a null list, so the built-in default would silently apply although the operator meant
/// "none". Allow-lists whose fallback is more permissive than "none" abort the start; lists whose fallback is at least
/// as restrictive only log a warning.
/// </summary>
public class EmptyListConfigurationValidatorTests
{
    private sealed class CapturingLogger : ILogger<GatewayEmptyListConfigurationValidator>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private static IConfiguration Json(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

    private static (ValidateOptionsResult Result, CapturingLogger Logger) Run(string json)
    {
        var config = Json(json);
        var logger = new CapturingLogger();
        var options = config.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
        return (new GatewayEmptyListConfigurationValidator(config, logger).Validate(null, options), logger);
    }

    public static IEnumerable<object[]> FailClosedKeys() =>
    [
        ["""{"Gateway":{"Authentication":{"ForwardAuth":{"AllowedRoles":[]}}}}""", "Gateway:Authentication:ForwardAuth:AllowedRoles"],
        ["""{"Gateway":{"Extensibility":{"BreakGlassAllowedRoles":[]}}}""", "Gateway:Extensibility:BreakGlassAllowedRoles"],
        ["""{"Gateway":{"SystemMetrics":{"AllowedRoles":[]}}}""", "Gateway:SystemMetrics:AllowedRoles"],
        ["""{"Gateway":{"WebSql":{"CrossSource":{"AllowedHttpAuthModes":[]}}}}""", "Gateway:WebSql:CrossSource:AllowedHttpAuthModes"],
        ["""{"Gateway":{"Authentication":{"BasicAuth":{"Session":{"AllowedEnvironments":[]}}}}}""", "Gateway:Authentication:BasicAuth:Session:AllowedEnvironments"],
    ];

    [Theory]
    [MemberData(nameof(FailClosedKeys))]
    public void ExplicitlyEmptyAllowlist_FailsStartup_NamingTheKey(string json, string key)
    {
        var (result, _) = Run(json);

        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldContain(key);
        result.FailureMessage!.ShouldContain("empty");
    }

    [Fact]
    public void EmptyForwardAuthRoles_MessageNamesSupportedWayToDisable()
    {
        var (result, _) = Run("""{"Gateway":{"Authentication":{"ForwardAuth":{"AllowedRoles":[]}}}}""");

        result.FailureMessage!.ShouldContain("ForwardAuth:Enabled");
    }

    [Fact]
    public void EmptySystemMetricsRoles_MessageNamesSupportedWayToDisable()
    {
        var (result, _) = Run("""{"Gateway":{"SystemMetrics":{"AllowedRoles":[]}}}""");

        result.FailureMessage!.ShouldContain("SystemMetrics:Enabled");
    }

    [Fact]
    public void EmptyReadOnlyScopes_OnlyWarns_BecauseFallbackIsMoreRestrictive()
    {
        var (result, logger) = Run("""{"Gateway":{"Authentication":{"EntraId":{"ReadOnlyScopes":[]}}}}""");

        result.Failed.ShouldBeFalse();
        logger.Warnings.ShouldContain(w => w.Contains("Gateway:Authentication:EntraId:ReadOnlyScopes"));
    }

    [Fact]
    public void EmptyKnownNetworks_OnlyWarns_BecauseLoopbackIsAlwaysTrusted()
    {
        var (result, logger) = Run("""{"Gateway":{"ReverseProxy":{"KnownNetworks":[]}}}""");

        result.Failed.ShouldBeFalse();
        logger.Warnings.ShouldContain(w => w.Contains("Gateway:ReverseProxy:KnownNetworks"));
    }

    [Fact]
    public void NonEmptyOrAbsentLists_PassWithoutWarnings()
    {
        var (result, logger) = Run("""
            {"Gateway":{"Authentication":{"ForwardAuth":{"AllowedRoles":["Analyst"]}},"SystemMetrics":{"Enabled":true}}}
            """);

        result.Succeeded.ShouldBeTrue();
        logger.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void NoGatewaySection_Passes()
    {
        Run("{}").Result.Succeeded.ShouldBeTrue();
    }
}
