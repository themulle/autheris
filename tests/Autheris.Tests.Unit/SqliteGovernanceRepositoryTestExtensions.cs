using System.Runtime.CompilerServices;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Autheris.Tests.Unit;

/// <summary>
/// Test access to the governance database behind a SQLite repository (tampering with rows, reading back state). The repository itself
/// opens a pooled connection per operation (WP-ARC-B-13); a test gets one dedicated connection per repository instance.
/// </summary>
internal static class SqliteGovernanceRepositoryTestExtensions
{
    private static readonly ConditionalWeakTable<SqliteGovernanceRepository, RawConnectionHolder> RawConnections = new();

    // Returns the connection to the pool when the repository (and with it the holder) is collected.
    private sealed class RawConnectionHolder(SqliteConnection connection)
    {
        public SqliteConnection Connection { get; } = connection;

        ~RawConnectionHolder() => Connection.Dispose();
    }

    /// <summary>One lazily opened connection to the repository's database, owned by the test (not by the repository).</summary>
    public static SqliteConnection RawConnection(this SqliteGovernanceRepository repository) =>
        RawConnections.GetValue(repository, r => new RawConnectionHolder(r.Connections.OpenAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult())).Connection;
}
