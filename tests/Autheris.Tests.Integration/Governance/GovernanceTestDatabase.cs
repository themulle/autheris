namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;

/// <summary>
/// Process-wide access to a migrated PostgreSQL governance template (WP-SIMPL-11). Web-host tests that configure the host
/// synchronously in a constructor or a static helper call <see cref="Create"/> to obtain an isolated database that is cloned from
/// the template, so no test runs the governance migrations itself. The template is built once per test process.
/// </summary>
internal static class GovernanceTestDatabase
{
    private static readonly ConcurrentDictionary<string, Lazy<string>> Stable = new(StringComparer.Ordinal);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository, string> Clones = new();

    private static readonly Lazy<Task<GovernancePostgresFixture>> Shared = new(() => Task.Run(StartAsync));

    /// <summary>Clones the migrated template into a new database and returns its connection string.</summary>
    public static async Task<string> CreateAsync(string name)
    {
        var fixture = await Shared.Value.ConfigureAwait(false);
        return await fixture.CreateDatabaseAsync(name).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the same cloned database for the same <paramref name="key"/> (for tests that start several hosts against one
    /// governance database, formerly keyed by the SQLite file path).
    /// </summary>
    public static string CreateStable(string key) =>
        Stable.GetOrAdd(key, static k => new Lazy<string>(() => Create("stable_" + System.IO.Path.GetFileNameWithoutExtension(k)))).Value;

    /// <summary>
    /// A connection string for the same cloned database that verifies the server certificate (<c>SSL Mode=VerifyCA</c> with the test CA),
    /// for hosts that run outside Development and therefore must satisfy ConnectionTlsPolicy.
    /// </summary>
    public static string WithVerifiedTls(string connectionString)
    {
        var ca = SharedDatabaseServers.PostgreSqlCaCertificatePath
            ?? throw new InvalidOperationException("Verified TLS needs the Testcontainers PostgreSQL server (AUTHERIS_TEST_POSTGRES is set).");
        return new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
        {
            SslMode = Npgsql.SslMode.VerifyCA,
            RootCertificate = ca
        }.ConnectionString;
    }

    /// <summary>Synchronous variant for host configuration callbacks.</summary>
    public static string Create(string name) => Task.Run(() => CreateAsync(name)).GetAwaiter().GetResult();

    /// <summary>Points the web host at a fresh cloned PostgreSQL governance database.</summary>
    public static void Apply(IWebHostBuilder builder, string name)
    {
        builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
        builder.UseSetting("Gateway:GovernanceDb:ConnectionString", Create(name));
    }

    /// <summary>
    /// Creates a migrated, initialized PostgreSQL governance repository on a fresh template clone. Replaces the SQLite
    /// <c>GovernanceStoreFactory.CreateSqlite</c> of the former unit tests; <paramref name="connectionString"/> addresses the clone
    /// (for raw SQL assertions).
    /// </summary>
    internal static Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository CreateRepository(
        Autheris.Application.Common.Interfaces.IEpochValidationService epochValidationService,
        out string connectionString,
        Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>? options = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
        Autheris.Application.Audit.IAuditChainAnchorStore? auditAnchorStore = null,
        Microsoft.Extensions.Logging.ILogger<Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository>? logger = null,
        Autheris.Application.Common.Interfaces.IEventBus? eventBus = null)
    {
        var gateway = options ?? Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions());
        connectionString = Create("repo_" + Guid.NewGuid().ToString("N")[..12]);
        // The options are init-only; the helper owns the freshly built options of a test, so reflection keeps the call sites unchanged.
        typeof(Autheris.Domain.Options.GovernanceDbOptions).GetProperty("Provider")!.SetValue(gateway.Value.GovernanceDb, "PostgreSql");
        typeof(Autheris.Domain.Options.GovernanceDbOptions).GetProperty("ConnectionString")!.SetValue(gateway.Value.GovernanceDb, connectionString);
        var repository = GovernanceStoreFactory.CreatePostgreSql(epochValidationService, gateway, environment, auditAnchorStore, logger, eventBus);
        Clones.Add(repository, connectionString);
        return repository;
    }

    /// <summary>Creates a repository on an existing clone (restart scenarios: several repository lifetimes over the same database).</summary>
    internal static Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository CreateRepositoryOn(
        string connectionString,
        Autheris.Application.Common.Interfaces.IEpochValidationService epochValidationService,
        Autheris.Application.Audit.IAuditChainAnchorStore? auditAnchorStore = null,
        Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>? options = null)
    {
        var gateway = options ?? Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions());
        typeof(Autheris.Domain.Options.GovernanceDbOptions).GetProperty("Provider")!.SetValue(gateway.Value.GovernanceDb, "PostgreSql");
        typeof(Autheris.Domain.Options.GovernanceDbOptions).GetProperty("ConnectionString")!.SetValue(gateway.Value.GovernanceDb, connectionString);
        var repository = GovernanceStoreFactory.CreatePostgreSql(epochValidationService, gateway, null, auditAnchorStore, null, null);
        Clones.Add(repository, connectionString);
        return repository;
    }

    /// <summary>Overload for tests that need no raw SQL access.</summary>
    internal static Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository CreateRepository(
        Autheris.Application.Common.Interfaces.IEpochValidationService epochValidationService,
        Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>? options = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
        Autheris.Application.Audit.IAuditChainAnchorStore? auditAnchorStore = null,
        Microsoft.Extensions.Logging.ILogger<Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository>? logger = null,
        Autheris.Application.Common.Interfaces.IEventBus? eventBus = null) =>
        CreateRepository(epochValidationService, out _, options, environment, auditAnchorStore, logger, eventBus);

    /// <summary>
    /// Runs raw SQL against a cloned database. With <paramref name="bypassAppendOnlyTriggers"/> the session skips triggers
    /// (<c>session_replication_role = replica</c>) so tests can simulate tampering with the append-only audit table.
    /// </summary>
    internal static int ExecuteRaw(string connectionString, string sql, bool bypassAppendOnlyTriggers = false, params (string Name, object Value)[] parameters)
    {
        using var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();
        if (bypassAppendOnlyTriggers)
        {
            using var set = conn.CreateCommand();
            set.CommandText = "SET session_replication_role = replica";
            set.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd.ExecuteNonQuery();
    }

    /// <summary>Runs a scalar query against a cloned database.</summary>
    internal static object? ScalarRaw(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    /// <summary>Connection string of the clone a repository created by <see cref="CreateRepository(Autheris.Application.Common.Interfaces.IEpochValidationService, Microsoft.Extensions.Options.IOptions{Autheris.Domain.Options.GatewayOptions}?, Microsoft.Extensions.Hosting.IHostEnvironment?, Autheris.Application.Audit.IAuditChainAnchorStore?, Microsoft.Extensions.Logging.ILogger{Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository}?, Autheris.Application.Common.Interfaces.IEventBus?)"/> is bound to.</summary>
    internal static string ConnectionStringOf(Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository repository) =>
        Clones.TryGetValue(repository, out var connectionString)
            ? connectionString
            : throw new InvalidOperationException("The repository was not created through GovernanceTestDatabase.");

    private static async Task<GovernancePostgresFixture> StartAsync()
    {
        var fixture = new GovernancePostgresFixture();
        await fixture.InitializeAsync().ConfigureAwait(false);
        return fixture;
    }
}

/// <summary>Raw-SQL access to the cloned PostgreSQL governance database of a test repository (replaces the SQLite <c>RawConnection</c>).</summary>
internal static class GovernanceRepositoryTestExtensions
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository, Npgsql.NpgsqlConnection> Raw = new();

    /// <summary>Returns one open connection per repository; it lives as long as the test process (clones are throwaway).</summary>
    public static Npgsql.NpgsqlConnection RawConnection(this Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository repository) =>
        Raw.GetValue(repository, static r =>
        {
            var connection = new Npgsql.NpgsqlConnection(GovernanceTestDatabase.ConnectionStringOf(r));
            connection.Open();
            return connection;
        });
}
