namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
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
        "Governance provider unsupported (OD-1). Accepted providers: PostgreSql, SqlServer. See docs/operations/migration-simplification.md.";

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

    [Fact]
    [Trait("Category", "Security")]
    public void Absent_provider_with_connection_string_resolves_postgresql_family()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["GovernanceDb:ConnectionString"] = "Host=localhost;Database=autheris;Username=postgres;Password=secret"
        });

        Should.NotThrow(() => GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(Environments.Production)));
        new Autheris.Domain.Options.GovernanceDbOptions().Provider.ShouldBe("PostgreSql");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("PostgreSql", "GovernanceDb:ConnectionString")]
    [InlineData("SqlServer", "GovernanceDb:ConnectionString")]
    [InlineData("PostgreSql", "Gateway:GovernanceDb:ConnectionString")]
    public void Provider_set_with_empty_connection_string_fails_startup(string provider, string connectionStringKey)
    {
        foreach (var environmentName in new[] { Environments.Development, "Test", Environments.Production })
        {
            foreach (var emptyValue in new string?[] { null, "", "   " })
            {
                var cfg = BuildConfig(new Dictionary<string, string?>
                {
                    [provider is "PostgreSql" && connectionStringKey.StartsWith("Gateway", StringComparison.Ordinal)
                        ? "Gateway:GovernanceDb:Provider"
                        : "GovernanceDb:Provider"] = provider,
                    [connectionStringKey] = emptyValue,
                    // a migration-only connection string must not satisfy the runtime requirement
                    ["GovernanceDb:MigrationConnectionString"] = "Host=localhost;Database=autheris;Username=m;Password=migration-secret"
                });

                var ex = Should.Throw<InvalidOperationException>(() =>
                    GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(environmentName)));
                ex.Message.ShouldBe(ExpectedMessage);
                ex.Message.ShouldNotContain("migration-secret");
            }
        }
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Postgres")]
    [InlineData("PgSql")]
    [InlineData("MsSql")]
    [InlineData("postgresql ")]
    public void Provider_alias_is_rejected_or_normalized_consistently(string provider)
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["GovernanceDb:Provider"] = provider,
            ["GovernanceDb:ConnectionString"] = "Host=localhost;Database=autheris;Username=postgres;Password=secret"
        });

        // the guard is strict for the runtime aliases; only exact family names (trim, ignore case) pass.
        if (provider.Trim().Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            Should.NotThrow(() => GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(Environments.Production)));
            return;
        }

        var ex = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(Environments.Production)));
        ex.Message.ShouldBe(ExpectedMessage);
        ex.Message.ShouldContain("PostgreSql");
        ex.Message.ShouldContain("SqlServer");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void No_bypass_switch_exists()
    {
        var names = typeof(Autheris.Domain.Options.GovernanceDbOptions).GetProperties().Select(p => p.Name)
            .Concat(typeof(GovernanceProviderGuard).GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Select(m => m.Name))
            .ToList();

        names.ShouldNotContain(n => n.Contains("Bypass", StringComparison.OrdinalIgnoreCase)
            || n.Contains("AllowSqlite", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Legacy", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Unsafe", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Skip", StringComparison.OrdinalIgnoreCase));

        // no environment variable or key changes the outcome: an in-memory override of a bypass-like key still fails.
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["GovernanceDb:Provider"] = "Sqlite",
            ["GovernanceDb:AllowSqlite"] = "true",
            ["Gateway:Dev:AllowSqliteGovernance"] = "true",
            ["GovernanceDb:ConnectionString"] = "Host=localhost;Database=a;Username=u;Password=p"
        });
        Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(Environments.Development)));
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
