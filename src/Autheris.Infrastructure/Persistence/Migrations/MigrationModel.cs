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

    /// <summary>Always applies the pending migrations, whatever <c>Migrations:Mode</c> says (the <c>--migrate-governance</c> job; issue #388).</summary>
    Task ApplyAsync(CancellationToken ct = default) => RunAsync(ct);

    /// <summary>Verifies without any DDL or write, with the checks and codes of <c>Mode = Verify</c> (<c>--verify-governance</c>).</summary>
    Task VerifyAsync(CancellationToken ct = default) => RunAsync(ct);

    /// <summary>Emits audit events that could not be written during the migration because the audit store was not ready yet (best effort; never throws for audit failures).</summary>
    Task EmitPendingAuditAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Governed error codes of the governance schema migrations (issue #388, plan D-7). They appear in logs and CLI output, never with connection details.</summary>
public static class GovernanceMigrationCodes
{
    public const string NewerThanBuild = "GOVERNANCE_SCHEMA_NEWER_THAN_BUILD";
    public const string ChecksumMismatch = "GOVERNANCE_SCHEMA_CHECKSUM_MISMATCH";
    public const string HistoryGap = "GOVERNANCE_SCHEMA_HISTORY_GAP";
    public const string HistoryDuplicate = "GOVERNANCE_SCHEMA_HISTORY_DUPLICATE";
    public const string HistoryRenamed = "GOVERNANCE_SCHEMA_HISTORY_RENAMED";
    public const string UnsupportedBase = "GOVERNANCE_SCHEMA_UNSUPPORTED_BASE";
    public const string Drift = "GOVERNANCE_SCHEMA_DRIFT";
    public const string Pending = "GOVERNANCE_SCHEMA_PENDING";
    public const string StepFailed = "GOVERNANCE_MIGRATION_STEP_FAILED";
    public const string LockTimeout = "GOVERNANCE_MIGRATION_LOCK_TIMEOUT";
    public const string LockFailed = "GOVERNANCE_MIGRATION_LOCK_FAILED";
    public const string NewerAllowedWarning = "GOVERNANCE_SCHEMA_NEWER_ALLOWED";
    public const string RuntimeDdl = "GOVERNANCE_MIGRATION_RUNTIME_DDL";
    public const string RuntimeLoginPrivileged = "GOVERNANCE_RUNTIME_LOGIN_PRIVILEGED";
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
    /// <summary>The shape change of the step (version 2 and higher; the frozen baseline has none). Folded onto the v0001 shape (plan D-5).</summary>
    internal GovernanceSchemaShapeDelta? ShapeDelta { get; init; }

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

    public GovernanceSchemaDriftException(string message) : this(GovernanceMigrationCodes.Drift, message) { }

    public GovernanceSchemaDriftException(string message, Exception innerException) : base(WithCode(GovernanceMigrationCodes.Drift, message), innerException)
    {
        Code = GovernanceMigrationCodes.Drift;
        Deviations = NoDeviations;
    }

    public GovernanceSchemaDriftException(string code, string message) : base(WithCode(code, message))
    {
        Code = code;
        Deviations = NoDeviations;
    }

    public GovernanceSchemaDriftException(string message, IReadOnlyList<SchemaDeviation> deviations, Exception? innerException = null)
        : this(GovernanceMigrationCodes.Drift, message, deviations, innerException)
    {
    }

    public GovernanceSchemaDriftException(string code, string message, IReadOnlyList<SchemaDeviation> deviations, Exception? innerException = null)
        : base(WithCode(code, message), innerException)
    {
        ArgumentNullException.ThrowIfNull(deviations);
        Code = code;
        Deviations = deviations;
    }

    /// <summary>The governed code (<see cref="GovernanceMigrationCodes"/>); the message starts with it.</summary>
    public string Code { get; }

    private static string WithCode(string code, string message) => code + ": " + message;

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
