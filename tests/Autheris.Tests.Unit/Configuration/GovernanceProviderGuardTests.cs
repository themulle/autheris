namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using Autheris.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GovernanceProviderGuardTests
{
    private const string ExpectedMessage =
        "Governance provider unsupported (OD-1). See docs/operations/migration-simplification.md.";

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Sqlite")]
    [InlineData(" sqlite ")]
    [InlineData("SQLITE")]
    public void Sqlite_provider_fails_startup_with_od1_message(string provider)
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["GovernanceDb:Provider"] = provider,
            ["GovernanceDb:ConnectionString"] = "Host=localhost;Database=autheris;Username=postgres;Password=secret"
        });

        var ex = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(Environments.Production)));

        ex.Message.ShouldBe(ExpectedMessage);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Data Source=x.db")]
    [InlineData("Data Source=x.sqlite")]
    [InlineData("Data Source=:memory:")]
    [InlineData("Server=localhost;Mode=Memory")]
    [InlineData("Filename=a.db")]
    [InlineData("Filename=a")]
    public void Sqlite_connection_string_with_other_provider_fails_startup(string connectionString)
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["GovernanceDb:Provider"] = "PostgreSql",
            ["GovernanceDb:ConnectionString"] = connectionString
        });

        var ex = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(Environments.Production)));

        ex.Message.ShouldBe(ExpectedMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Missing_provider_and_connection_string_fails_closed()
    {
        var emptyCfg = BuildConfig(new Dictionary<string, string?>());

        var ex = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(emptyCfg, new StubHostEnvironment(Environments.Production)));

        ex.Message.ShouldBe(ExpectedMessage);

        var whitespaceCfg = BuildConfig(new Dictionary<string, string?>
        {
            ["GovernanceDb:Provider"] = "   ",
            ["GovernanceDb:ConnectionString"] = "   "
        });

        var whitespaceEx = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(whitespaceCfg, new StubHostEnvironment(Environments.Production)));

        whitespaceEx.Message.ShouldBe(ExpectedMessage);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Oracle")]
    [InlineData("DuckDb")]
    [InlineData("MySql")]
    [InlineData("UnknownEngine")]
    public void Unknown_provider_fails_startup(string provider)
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["GovernanceDb:Provider"] = provider,
            ["GovernanceDb:ConnectionString"] = "Host=localhost;Database=autheris"
        });

        var ex = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(Environments.Production)));

        ex.Message.ShouldBe(ExpectedMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Error_message_never_contains_connection_string_value()
    {
        const string secretCanary = "SuperSecretConnValue_987654321";
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["GovernanceDb:Provider"] = "PostgreSql",
            ["GovernanceDb:ConnectionString"] = $"Data Source=/var/lib/{secretCanary}.db;Password={secretCanary}"
        });

        var ex = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(Environments.Production)));

        ex.Message.ShouldBe(ExpectedMessage);
        ex.Message.ShouldNotContain(secretCanary);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("PostgreSql", "Host=localhost;Database=autheris;Username=postgres;Password=secret")]
    [InlineData(" postgresql ", "Host=localhost;Database=autheris;Username=postgres;Password=secret")]
    [InlineData("SqlServer", "Server=localhost;Database=autheris;User Id=sa;Password=secret")]
    [InlineData("SQLSERVER", "Server=localhost;Database=autheris;User Id=sa;Password=secret")]
    [InlineData(null, "Host=localhost;Database=autheris;Username=postgres;Password=secret")]
    public void PostgreSql_and_SqlServer_pass(string? provider, string connectionString)
    {
        var settings = new Dictionary<string, string?>
        {
            ["GovernanceDb:ConnectionString"] = connectionString
        };
        if (provider is not null)
        {
            settings["GovernanceDb:Provider"] = provider;
        }

        var cfg = BuildConfig(settings);

        Should.NotThrow(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(Environments.Production)));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    [InlineData("Test")]
    public void Development_environment_has_no_exemption(string environmentName)
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["GovernanceDb:Provider"] = "Sqlite",
            ["GovernanceDb:ConnectionString"] = "Data Source=:memory:"
        });

        var ex = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(environmentName)));

        ex.Message.ShouldBe(ExpectedMessage);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
