namespace Autheris.Tests.Integration;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Helpers for the governance schema shape, post-condition and least-privilege integration tests (issue #94, stream A).</summary>
internal static class GovernanceSchemaTestSupport
{
    public static IOptions<GatewayOptions> PostgreSqlOptions(string connectionString, GovernanceMigrationMode mode) => Options.Create(new GatewayOptions
    {
        GovernanceDb = new GovernanceDbOptions
        {
            Provider = "PostgreSql",
            ConnectionString = connectionString,
            EnableOutboxProcessor = false,
            SeedDemoData = false,
            Migrations = new GovernanceMigrationOptions { Mode = mode }
        }
    });

    public static IOptions<GatewayOptions> SqlServerOptions(string connectionString, GovernanceMigrationMode mode) => Options.Create(new GatewayOptions
    {
        GovernanceDb = new GovernanceDbOptions
        {
            Provider = "SqlServer",
            ConnectionString = connectionString,
            EnableOutboxProcessor = false,
            SeedDemoData = false,
            Migrations = new GovernanceMigrationOptions { Mode = mode }
        }
    });

    private static IHostEnvironment Env()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        return env;
    }

    public static PostgreSqlGovernanceRepository UnmigratedPostgreSql(IOptions<GatewayOptions> options)
    {
        var env = Env();
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        return new PostgreSqlGovernanceRepository(
            Substitute.For<IEpochValidationService>(), options, env, secrets,
            AuditChainAnchorStoreFactory.CreateForGovernance(options.Value, env, secrets, null, null), Substitute.For<ICallerContextProvider>());
    }

    public static SqlServerGovernanceRepository UnmigratedSqlServer(IOptions<GatewayOptions> options)
    {
        var env = Env();
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        return new SqlServerGovernanceRepository(
            Substitute.For<IEpochValidationService>(), options, env, secrets,
            AuditChainAnchorStoreFactory.CreateForGovernance(options.Value, env, secrets, null, null), Substitute.For<ICallerContextProvider>());
    }

    /// <summary>Runs the migration runner of the engine against the connection string in the given mode.</summary>
    public static async Task RunPostgreSqlAsync(string connectionString, GovernanceMigrationMode mode)
    {
        var options = PostgreSqlOptions(connectionString, mode);
        using var repository = UnmigratedPostgreSql(options);
        await new PostgreSqlGovernanceMigrationRunner(repository, options).RunAsync();
    }

    public static async Task RunSqlServerAsync(string connectionString, GovernanceMigrationMode mode)
    {
        var options = SqlServerOptions(connectionString, mode);
        using var repository = UnmigratedSqlServer(options);
        await new SqlServerGovernanceMigrationRunner(repository, options).RunAsync();
    }

    /// <summary>
    /// Plan D-2: the embedded snapshot must equal the shape read from a database that holds the baseline. On a mismatch the actual
    /// snapshot is written to <c>AUTHERIS_SHAPE_SNAPSHOT_DIR</c> (default: temp) and the test fails.
    /// </summary>
    public static void AssertSnapshotEquals(string engine, GovernanceSchemaShape actual)
    {
        var actualJson = GovernanceSchemaShapeLoader.Serialize(actual);
        var resource = GovernanceSchemaShapeLoader.ResourceName(engine);
        using var stream = typeof(GovernanceSchemaShapeLoader).Assembly.GetManifestResourceStream(resource);
        var expectedJson = stream is null ? null : new System.IO.StreamReader(stream).ReadToEnd().ReplaceLineEndings("\n");
        if (expectedJson != actualJson)
        {
            var dir = Environment.GetEnvironmentVariable("AUTHERIS_SHAPE_SNAPSHOT_DIR") ?? System.IO.Path.GetTempPath();
            var path = System.IO.Path.Combine(dir, resource);
            System.IO.File.WriteAllText(path, actualJson);
            Assert.Fail($"The embedded snapshot '{resource}' differs from the migrated schema. The actual snapshot was written to '{path}'; copy it to src/Autheris.Infrastructure/Persistence/Migrations/Shape/.");
        }
    }
}
