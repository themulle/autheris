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
    [InlineData("PostgreSql", "Host=localhost;Database=autheris;Username=postgres;Password=secret;SSL Mode=VerifyFull")]
    [InlineData(" postgresql ", "Host=localhost;Database=autheris;Username=postgres;Password=secret;SSL Mode=VerifyFull")]
    [InlineData("SqlServer", "Server=localhost;Database=autheris;User Id=sa;Password=secret")]
    [InlineData("SQLSERVER", "Server=localhost;Database=autheris;User Id=sa;Password=secret")]
    [InlineData(null, "Host=localhost;Database=autheris;Username=postgres;Password=secret;SSL Mode=VerifyFull")]
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
            ["GovernanceDb:ConnectionString"] = "Host=localhost;Database=autheris;Username=postgres;Password=secret;SSL Mode=VerifyFull"
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
            ["GovernanceDb:ConnectionString"] = "Host=localhost;Database=autheris;Username=postgres;Password=secret;SSL Mode=VerifyFull"
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

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("PostgreSql", "Host=db.internal;Database=autheris;Username=postgres;Password=SecretCanary_991;SSL Mode=Disable", "SSL Mode=VerifyFull")]
    [InlineData("PostgreSql", "Host=db.internal;Database=autheris;Username=postgres;Password=SecretCanary_991;SSL Mode=Require", "SSL Mode=VerifyFull")]
    [InlineData("PostgreSql", "Host=db.internal;Database=autheris;Username=postgres;Password=SecretCanary_991;SSL Mode=VerifyCA", "SSL Mode=VerifyFull")]
    [InlineData("PostgreSql", "Host=db.internal;Database=autheris;Username=postgres;Password=SecretCanary_991;SSL Mode=VerifyFull;Trust Server Certificate=true", "TrustServerCertificate")]
    [InlineData("SqlServer", "Server=db.internal;Database=autheris;User Id=sa;Password=SecretCanary_991;Encrypt=Optional", "Encrypt=Mandatory")]
    [InlineData("SqlServer", "Server=db.internal;Database=autheris;User Id=sa;Password=SecretCanary_991;Encrypt=Mandatory;TrustServerCertificate=true", "TrustServerCertificate=true")]
    public void Governance_connection_without_tls_fails_startup_outside_development(string provider, string connectionString, string expectedMention)
    {
        foreach (var environmentName in new[] { Environments.Production, Environments.Staging, "Test" })
        {
            var cfg = BuildConfig(new Dictionary<string, string?>
            {
                ["Gateway:GovernanceDb:Provider"] = provider,
                ["Gateway:GovernanceDb:ConnectionString"] = connectionString
            });

            var ex = Should.Throw<InvalidOperationException>(() =>
                GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(environmentName)));
            ex.Message.ShouldContain(expectedMention);
            ex.Message.ShouldContain("ConnectionString");
            ex.Message.ShouldNotContain("SecretCanary_991");
            ex.Message.ShouldNotContain("db.internal");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Null_environment_is_treated_as_production()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = "Host=db.internal;Database=autheris;Username=postgres;Password=SecretCanary_991;SSL Mode=VerifyCA"
        });

        var ex = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(cfg, env: null));
        ex.Message.ShouldContain("SSL Mode=VerifyFull");
        ex.Message.ShouldNotContain("SecretCanary_991");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Development_allows_plain_connection()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = "Host=localhost;Database=autheris;Username=postgres;Password=dev",
            ["Gateway:GovernanceDb:MigrationConnectionString"] = "Host=localhost;Database=autheris;Username=migrator;Password=dev"
        });

        Should.NotThrow(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(Environments.Development)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Governance_empty_provider_is_postgresql_and_checked()
    {
        // Absent provider uses PostgreSql default and enforces VerifyFull outside Development.
        var absentProviderWeakTls = BuildConfig(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:ConnectionString"] = "Host=db.internal;Database=autheris;Username=postgres;Password=SecretCanary_991;SSL Mode=Require"
        });

        Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(absentProviderWeakTls, new StubHostEnvironment(Environments.Production)));

        // Explicit empty provider is rejected even when the connection string has VerifyFull.
        var explicitEmptyProvider = BuildConfig(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:Provider"] = "",
            ["Gateway:GovernanceDb:ConnectionString"] = "Host=db.internal;Database=autheris;Username=postgres;Password=SecretCanary_991;SSL Mode=VerifyFull"
        });

        Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(explicitEmptyProvider, new StubHostEnvironment(Environments.Production)));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("GovernanceDb:MigrationConnectionString", "Host=db.internal;Database=autheris;Username=ddl;Password=SecretCanary_991;SSL Mode=VerifyCA")]
    [InlineData("Gateway:GovernanceDb:MigrationConnectionString", "Host=db.internal;Database=autheris;Username=ddl;Password=SecretCanary_991;SSL Mode=Require")]
    public void Governance_migration_connection_without_verifyfull_rejected(string migrationKey, string migrationConnectionString)
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = "Host=db.internal;Database=autheris;Username=app;Password=app-pw;SSL Mode=VerifyFull",
            [migrationKey] = migrationConnectionString
        });

        var ex = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(cfg, new StubHostEnvironment(Environments.Production)));
        ex.Message.ShouldContain("MigrationConnectionString");
        ex.Message.ShouldContain("SSL Mode=VerifyFull");
        ex.Message.ShouldNotContain("SecretCanary_991");
        ex.Message.ShouldNotContain("db.internal");

        var validCfg = BuildConfig(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = "Host=db.internal;Database=autheris;Username=app;Password=app-pw;SSL Mode=VerifyFull",
            [migrationKey] = "Host=db.internal;Database=autheris;Username=ddl;Password=ddl-pw;SSL Mode=VerifyFull"
        });

        Should.NotThrow(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(validCfg, new StubHostEnvironment(Environments.Production)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Conflicting_provider_aliases_fail_startup()
    {
        var providerConflict = BuildConfig(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["GovernanceDb:Provider"] = "SqlServer",
            ["Gateway:GovernanceDb:ConnectionString"] = "Host=db.internal;Database=autheris;SSL Mode=VerifyFull"
        });

        var ex = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(providerConflict, new StubHostEnvironment(Environments.Production)));
        ex.Message.ShouldContain("Provider");

        var connConflict = BuildConfig(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = "Host=db1.internal;Database=autheris;Password=SecretCanary_1;SSL Mode=VerifyFull",
            ["GovernanceDb:ConnectionString"] = "Host=db2.internal;Database=autheris;Password=SecretCanary_2;SSL Mode=VerifyFull"
        });

        var connEx = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(connConflict, new StubHostEnvironment(Environments.Production)));
        connEx.Message.ShouldContain("ConnectionString");
        connEx.Message.ShouldNotContain("SecretCanary_1");
        connEx.Message.ShouldNotContain("SecretCanary_2");

        var migConflict = BuildConfig(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = "Host=db1.internal;Database=autheris;SSL Mode=VerifyFull",
            ["Gateway:GovernanceDb:MigrationConnectionString"] = "Host=db1.internal;Database=autheris;Password=SecretCanary_1;SSL Mode=VerifyFull",
            ["GovernanceDb:MigrationConnectionString"] = "Host=db2.internal;Database=autheris;Password=SecretCanary_2;SSL Mode=VerifyFull"
        });

        var migEx = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(migConflict, new StubHostEnvironment(Environments.Production)));
        migEx.Message.ShouldContain("MigrationConnectionString");
        migEx.Message.ShouldNotContain("SecretCanary_1");
        migEx.Message.ShouldNotContain("SecretCanary_2");
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
