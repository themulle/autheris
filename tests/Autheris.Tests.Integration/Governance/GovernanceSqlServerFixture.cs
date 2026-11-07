using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Governance;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.MsSql;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

[CollectionDefinition(Name)]
public sealed class GovernanceSqlServerCollection : ICollectionFixture<GovernanceSqlServerFixture>
{
    public const string Name = "GovernanceSqlServer";
}

/// <summary>
/// Shared SQL Server 2022 container fixture for governance repository integration tests.
/// Starts one container per test run (or connects to <c>AUTHERIS_TEST_MSSQL</c>) and provisions
/// isolated per-test databases migrated via <see cref="SqlServerGovernanceMigrationRunner"/>.
/// </summary>
public sealed class GovernanceSqlServerFixture : IAsyncLifetime, IGovernanceSqlServerFixture
{
    public const string PinnedSqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    private MsSqlContainer? _container;
    private string? _adminConnectionString;
    private readonly ConcurrentBag<string> _createdDatabases = new();

    public bool IsAvailable { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("AUTHERIS_TEST_MSSQL");
        try
        {
            if (!string.IsNullOrWhiteSpace(external))
            {
                var builder = new SqlConnectionStringBuilder(external)
                {
                    InitialCatalog = "master",
                    TrustServerCertificate = true
                };
                _adminConnectionString = builder.ConnectionString;
            }
            else
            {
                var reuse = string.Equals(
                    Environment.GetEnvironmentVariable("TESTCONTAINERS_REUSE_ENABLE"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);

                _container = new MsSqlBuilder(PinnedSqlServerImage)
                    .WithPassword(NewPassword())
                    .WithReuse(reuse)
                    .WithLabel("autheris.tests", "governance-sqlserver")
                    .Build();

                await _container.StartAsync().ConfigureAwait(false);
                var builder = new SqlConnectionStringBuilder(_container.GetConnectionString())
                {
                    InitialCatalog = "master",
                    TrustServerCertificate = true
                };
                _adminConnectionString = builder.ConnectionString;
            }

            await using var conn = new SqlConnection(_adminConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            _ = await cmd.ExecuteScalarAsync().ConfigureAwait(false);

            IsAvailable = true;
        }
        catch (Exception ex)
        {
            ContainerStartup.FailIfRequired(ex, "The SQL Server governance fixture");
            IsAvailable = false;
            UnavailableReason = ex.Message;
        }
    }

    public async Task<string> CreateDatabaseAsync(string? name = null)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(_adminConnectionString))
        {
            throw new InvalidOperationException(
                $"GovernanceSqlServerFixture is not available: {UnavailableReason ?? "container did not start"}.");
        }

        var dbName = GovernanceSqlServerDatabaseName.Sanitize(name);
        await using (var conn = new SqlConnection(_adminConnectionString))
        {
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{dbName}]";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        _createdDatabases.Add(dbName);

        var dbBuilder = new SqlConnectionStringBuilder(_adminConnectionString)
        {
            InitialCatalog = dbName,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 20,
            TrustServerCertificate = true
        };
        var dbConnStr = dbBuilder.ConnectionString;
        await MigrateDatabaseAsync(dbConnStr).ConfigureAwait(false);
        return dbConnStr;
    }

    public async Task DisposeAsync()
    {
        if (_container is null && !string.IsNullOrWhiteSpace(_adminConnectionString))
        {
            while (_createdDatabases.TryTake(out var db))
            {
                try
                {
                    await DropDatabaseAsync(db).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup on external SQL Server instance.
                }
            }
        }

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task MigrateDatabaseAsync(string connectionString)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "autheris_mssql_tmpl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var gatewayOptions = new GatewayOptions
            {
                Audit = new AuditOptions
                {
                    ChainAnchorPath = Path.Combine(tempDir, "audit.anchor")
                },
                GovernanceDb = new GovernanceDbOptions
                {
                    Provider = "SqlServer",
                    ConnectionString = connectionString,
                    SeedDemoData = false,
                    AuditHmacKeyVaultRef = "test-audit-hmac-secret",
                    Migrations = new GovernanceMigrationOptions
                    {
                        Mode = GovernanceMigrationMode.Apply
                    }
                }
            };
            var options = Options.Create(gatewayOptions);
            var env = new StubHostEnvironment { EnvironmentName = Environments.Development, ContentRootPath = tempDir };
            var secrets = new StubKeyVaultSecretProvider();
            var anchorStore = AuditChainAnchorStoreFactory.CreateForGovernance(options.Value, env, secrets, null, null);
            await using var repo = new SqlServerGovernanceRepository(
                new FakeEpochValidationService(),
                options,
                env,
                secrets,
                anchorStore,
                new StubCallerContextProvider(),
                NullLogger<SqlServerGovernanceRepository>.Instance);
            var runner = new SqlServerGovernanceMigrationRunner(
                repo,
                options,
                NullLogger<SqlServerGovernanceMigrationRunner>.Instance,
                env);
            await runner.ApplyAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of temp anchor directory.
            }
        }
    }

    private async Task DropDatabaseAsync(string dbName)
    {
        if (string.IsNullOrWhiteSpace(_adminConnectionString))
        {
            return;
        }

        await using var conn = new SqlConnection(_adminConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF DB_ID(N'{dbName}') IS NOT NULL BEGIN ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]; END";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string NewPassword()
    {
        Span<byte> raw = stackalloc byte[18];
        RandomNumberGenerator.Fill(raw);
        return "Au!" + Convert.ToBase64String(raw).Replace('+', 'A').Replace('/', 'b').TrimEnd('=') + "9a";
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubKeyVaultSecretProvider : IKeyVaultSecretProvider
    {
        private static readonly byte[] TestKey = new byte[32];
        public byte[] GetSecretBytes(string secretName) => (byte[])TestKey.Clone();
    }

    private sealed class StubCallerContextProvider : ICallerContextProvider
    {
        public CallerContext? ResolveCallerContext() => null;
    }
}
