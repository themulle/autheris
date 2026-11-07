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

    /// <summary>Synchronous variant for host configuration callbacks.</summary>
    public static string Create(string name) => Task.Run(() => CreateAsync(name)).GetAwaiter().GetResult();

    /// <summary>Points the web host at a fresh cloned PostgreSQL governance database.</summary>
    public static void Apply(IWebHostBuilder builder, string name)
    {
        builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
        builder.UseSetting("Gateway:GovernanceDb:ConnectionString", Create(name));
    }

    private static async Task<GovernancePostgresFixture> StartAsync()
    {
        var fixture = new GovernancePostgresFixture();
        await fixture.InitializeAsync().ConfigureAwait(false);
        return fixture;
    }
}
