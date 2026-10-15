using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>Runs the ordered governance schema migrations (WP-ARC-B-12).</summary>
public interface IGovernanceMigrationRunner
{
    /// <summary>Applies, verifies or skips the pending migrations according to <c>GovernanceDb:Migrations:Mode</c>. Throws when they cannot be satisfied.</summary>
    Task RunAsync(CancellationToken ct = default);
}

/// <summary>Post-migration initialization of a governance store (audit tail, chain anchor, optional demo seed, audit processor start).</summary>
public interface IGovernanceStoreInitializer
{
    Task InitializeAsync(CancellationToken ct = default);
}

/// <summary>Context handed to a migration step. <see cref="Transaction"/> is set when the step runs in its own transaction.</summary>
public sealed record MigrationContext(DbConnection Connection, DbTransaction? Transaction, ILogger? Logger, CancellationToken CancellationToken);

/// <summary>
/// One ordered schema migration. The <see cref="Checksum"/> is a declared identity token of the step content; it is stored with the
/// applied version and a mismatch on a later start is treated as schema drift (startup fails).
/// While no installation is in production, the baseline step 0001 is extended in place and keeps its identity token (owner decision,
/// no upgrade or repair migrations); an existing database that does not match the extended baseline is detected by the read-only
/// schema shape verifier instead of being altered. Once an installation is in production, add a new, higher version instead.
/// </summary>
public sealed record MigrationStep(int Version, string Name, string Checksum, Func<MigrationContext, Task> Apply, bool Transactional = false)
{
    public static string ComputeChecksum(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}

/// <summary>
/// The applied schema differs from what this binary expects (checksum mismatch, newer database, pending steps in Verify mode, or a
/// schema shape that does not match the baseline). <see cref="Deviations"/> lists the shape differences (identifiers only).
/// </summary>
public sealed class GovernanceSchemaDriftException : InvalidOperationException
{
    private static readonly IReadOnlyList<SchemaDeviation> NoDeviations = [];

    public GovernanceSchemaDriftException(string message) : base(message) { Deviations = NoDeviations; }

    public GovernanceSchemaDriftException(string message, Exception innerException) : base(message, innerException) { Deviations = NoDeviations; }

    public GovernanceSchemaDriftException(string message, IReadOnlyList<SchemaDeviation> deviations, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(deviations);
        Deviations = deviations;
    }

    /// <summary>The differences between the expected and the actual schema shape; empty for checksum or version drift.</summary>
    public IReadOnlyList<SchemaDeviation> Deviations { get; }
}

/// <summary>
/// A governance store was used before schema migration and initialization completed. Fail closed: no read or write is served
/// from a store whose schema, audit chain tail and chain anchor have not been established (WP-ARC-B-12).
/// </summary>
public sealed class GovernanceStoreNotReadyException(string store) : InvalidOperationException(
    $"The governance store '{store}' was used before schema migration and initialization completed (fail closed).")
{
}
