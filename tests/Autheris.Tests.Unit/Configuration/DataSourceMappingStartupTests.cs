namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>#668: <c>WebSql:DataSourceMappings</c> is one hop to an existing connection; a dbt mapping WebSQL cannot reach is reported.</summary>
public sealed class DataSourceMappingStartupTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
    private static GatewayOptions Gateway(Dictionary<string, string> mappings, params string[] connections)
    {
        var gateway = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true } };
        foreach (var name in connections)
        {
            gateway.DataSources.Connections[name] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = "x" };
        }

        foreach (var (key, value) in mappings)
        {
            gateway.WebSql.DataSourceMappings[key] = value;
        }

        return gateway;
    }

    [Fact]
    public void ValidMapping_DoesNotFail()
    {
        var gateway = Gateway(new() { ["sales"] = "pgdata" }, "pgdata");

        Should.NotThrow(() => GatewayStartupValidator.ValidateDataSourceMappings(gateway));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Mapping_TargetThatIsAlsoMappingKey_FailsStartup()
    {
        var gateway = Gateway(new() { ["a"] = "b", ["b"] = "c" }, "a", "b", "c");

        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDataSourceMappings(gateway));

        ex.Message.ShouldContain("DataSourceMappings");
        ex.Message.ShouldContain("DataSourceMappings:a");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Mapping_Cycle_FailsStartup_AndResolvesToNull()
    {
        var gateway = Gateway(new() { ["a"] = "b", ["b"] = "a" }, "a", "b");

        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDataSourceMappings(gateway));
        new SqlDataSourceResolver(Options.Create(gateway)).ResolveConnectionKey("a").ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("missing")]
    [Trait("Category", "Security")]
    public void Mapping_EmptyOrUnknownTarget_FailsStartup(string target)
    {
        var gateway = Gateway(new() { ["sales"] = target }, "pgdata");

        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDataSourceMappings(gateway));
    }

    [Fact]
    public void DbtMappedDatabaseInAllowedDataSources_WithoutWebSqlMapping_Warns()
    {
        var gateway = Gateway(new(), "pgdata");
        gateway.Dbt.DatabaseMappings["sales"] = "pgdata";
        gateway.WebSql.AllowedDataSources.Add("sales");
        var logger = new CapturingLogger();

        GatewayStartupValidator.ValidateDataSourceMappings(gateway, logger);

        logger.Levels.ShouldContain(LogLevel.Warning);
    }

    [Fact]
    public void DbtMappedDatabase_WithWebSqlMappingOrNotAllowed_DoesNotWarn()
    {
        var mapped = Gateway(new() { ["sales"] = "pgdata" }, "pgdata");
        mapped.Dbt.DatabaseMappings["sales"] = "pgdata";
        mapped.WebSql.AllowedDataSources.Add("sales");
        var notAllowed = Gateway(new(), "pgdata");
        notAllowed.Dbt.DatabaseMappings["sales"] = "pgdata";
        var logger = new CapturingLogger();

        GatewayStartupValidator.ValidateDataSourceMappings(mapped, logger);
        GatewayStartupValidator.ValidateDataSourceMappings(notAllowed, logger);

        logger.Levels.ShouldBeEmpty();
    }
}
