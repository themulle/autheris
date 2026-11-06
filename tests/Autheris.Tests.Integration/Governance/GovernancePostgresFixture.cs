namespace Autheris.Tests.Integration.Governance;

using System;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Governance;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

[CollectionDefinition(Name)]
public sealed class GovernancePostgresCollection : ICollectionFixture<GovernancePostgresFixture>
{
    public const string Name = "GovernancePostgres";
}

/// <summary>
/// Shared PostgreSQL Testcontainers fixture that runs <see cref="PostgreSqlGovernanceMigrationRunner"/> once into
/// <c>autheris_template</c> and clones isolated per-test-class databases via <c>CREATE DATABASE ... TEMPLATE autheris_template</c>.
/// </summary>
public sealed class GovernancePostgresFixture : IAsyncLifetime, IGovernancePostgresFixture
{
    public const string PinnedPostgresImage =
        "postgres:17-alpine@sha256:6f30057d31f5861b66f3545d4821f987aacf1dd920765f0acaded0c5ceff975c";

    private PostgreSqlContainer? _ownedContainer;
    private string _adminConnectionString = string.Empty;

    public bool IsAvailable { get; private set; }

    public string TemplateDatabaseName => GovernancePostgresDatabaseName.DefaultTemplateName;

    public async Task InitializeAsync()
    {
        try
        {
            var envConn = Environment.GetEnvironmentVariable("AUTHERIS_TEST_POSTGRES");
            if (!string.IsNullOrWhiteSpace(envConn))
            {
                _adminConnectionString = envConn;
            }
            else
            {
                bool reuse = !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
                _ownedContainer = new PostgreSqlBuilder(PinnedPostgresImage)
                    .WithReuse(reuse)
                    .WithLabel("autheris.tests", "governance-postgresql-template")
                    .Build();
                await _ownedContainer.StartAsync();
                _adminConnectionString = _ownedContainer.GetConnectionString();
            }

            await InitializeTemplateDatabaseAsync();
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL governance template fixture");
        }
    }

    public async Task<string> CreateDatabaseAsync(string? name = null)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("PostgreSQL governance fixture is not available.");
        }

        var dbName = GovernancePostgresDatabaseName.Sanitize(name);
        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await TerminateBackendsAsync(admin, TemplateDatabaseName);
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{dbName}\" TEMPLATE \"{TemplateDatabaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = dbName
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_ownedContainer is not null)
        {
            await _ownedContainer.DisposeAsync();
        }
    }

    private async Task InitializeTemplateDatabaseAsync()
    {
        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await TerminateBackendsAsync(admin, TemplateDatabaseName);
            await using (var drop = admin.CreateCommand())
            {
                drop.CommandText = $"DROP DATABASE IF EXISTS \"{TemplateDatabaseName}\" WITH (FORCE)";
                await drop.ExecuteNonQueryAsync();
            }

            await using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{TemplateDatabaseName}\"";
                await create.ExecuteNonQueryAsync();
            }
        }

        var templateConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = TemplateDatabaseName
        }.ConnectionString;

        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = templateConnectionString,
                SeedDemoData = false,
                Migrations = new GovernanceMigrationOptions
                {
                    Mode = GovernanceMigrationMode.Apply
                }
            }
        });

        var env = new StubHostEnvironment("Test");
        var secrets = new StubKeyVaultSecretProvider();
        var anchorStore = AuditChainAnchorStoreFactory.CreateForGovernance(options.Value, env, secrets, null, null);
        await using (var repository = new PostgreSqlGovernanceRepository(
            new FakeEpochValidationService(),
            options,
            env,
            secrets,
            anchorStore,
            new StubCallerContextProvider()))
        {
            var runner = new PostgreSqlGovernanceMigrationRunner(repository, options, environment: env);
            await runner.RunAsync();
        }

        NpgsqlConnection.ClearPool(new NpgsqlConnection(templateConnectionString));
    }

    private static async Task TerminateBackendsAsync(NpgsqlConnection admin, string databaseName)
    {
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid()";
        cmd.Parameters.AddWithValue("db", databaseName);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Autheris.Tests.Integration";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubKeyVaultSecretProvider : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) => new byte[32];
    }

    private sealed class StubCallerContextProvider : ICallerContextProvider
    {
        public CallerContext? ResolveCallerContext() => null;
    }
}
