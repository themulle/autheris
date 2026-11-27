namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions.DependencyInjection.StartupChecks;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GraphQlTableRootsOnlyScopesStartupTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static GatewayOptions Gateway(bool introspection, params string[] scopes) => new()
    {
        GraphQL = new GraphQLOptions { EnableIntrospection = introspection, TableRootsOnlyScopes = [.. scopes] }
    };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Agent Read")]
    [InlineData("Agent.Read\t")]
    [InlineData("api://app/")]
    public void Blank_or_whitespace_or_unmatchable_entry_fails_startup(string entry)
    {
        var ex = Should.Throw<ValidationException>(() =>
            GraphQlTableRootsOnlyScopesStartup.ValidateAndReport(Gateway(false, "Agent.Read", entry), new CapturingLogger()));
        ex.Message.ShouldContain("TableRootsOnlyScopes");
    }

    [Fact]
    public void Null_entry_fails_startup()
    {
        var gateway = new GatewayOptions { GraphQL = new GraphQLOptions { TableRootsOnlyScopes = [null!] } };
        Should.Throw<ValidationException>(() => GraphQlTableRootsOnlyScopesStartup.ValidateAndReport(gateway, null));
    }

    [Fact]
    public void Url_form_entry_is_accepted_and_names_the_scope()
    {
        var logger = new CapturingLogger();
        GraphQlTableRootsOnlyScopesStartup.ValidateAndReport(Gateway(false, "api://app/Agent.Read", "Agent.Read"), logger);
        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Information);
        logger.Entries[0].Text.ShouldContain("api://app/Agent.Read");
    }

    [Fact]
    public void Empty_list_logs_nothing()
    {
        var logger = new CapturingLogger();
        GraphQlTableRootsOnlyScopesStartup.ValidateAndReport(Gateway(true), logger);
        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Introspection_with_configured_scopes_logs_a_warning()
    {
        var logger = new CapturingLogger();
        GraphQlTableRootsOnlyScopesStartup.ValidateAndReport(Gateway(true, "Agent.Read"), logger);
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning);
    }
}
