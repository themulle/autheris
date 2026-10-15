using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>
/// Dialect-neutral migration engine: takes the schema lock, re-reads the applied versions under the lock, verifies checksums and
/// version ordering, applies pending steps and records each one in <c>autheris_schema_migrations</c>. Fail closed on any error.
/// </summary>
public abstract class GovernanceMigrationRunnerBase : IGovernanceMigrationRunner
{
    private readonly GovernanceMigrationOptions _options;
    private readonly ILogger? _logger;

    protected GovernanceMigrationRunnerBase(GovernanceMigrationOptions options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
    }

    /// <summary>The ordered migration catalogue of the dialect.</summary>
    public abstract IReadOnlyList<MigrationStep> Steps { get; }

    protected abstract string CreateVersionTableSql { get; }

    protected abstract string SelectVersionsSql { get; }

    protected abstract string InsertVersionSql { get; }

    /// <summary>When false the connection is owned by the caller (SQLite shared governance connection) and not disposed.</summary>
    protected virtual bool OwnsConnection => true;

    protected abstract Task<DbConnection> OpenConnectionAsync(CancellationToken ct);

    /// <summary>Acquires the cross-process schema lock. It is held for the whole run.</summary>
    protected abstract Task AcquireLockAsync(DbConnection connection, CancellationToken ct);

    /// <summary>Releases the schema lock; <paramref name="success"/> tells a transaction-based lock whether to commit.</summary>
    protected abstract Task ReleaseLockAsync(DbConnection connection, bool success, CancellationToken ct);

    /// <summary>The read-only shape verifier of the engine (plan D-1). Runs in Apply and Verify mode, under the schema lock.</summary>
    internal abstract GovernanceSchemaShapeVerifier ShapeVerifier { get; }

    /// <summary>Engine name used in the drift message.</summary>
    protected abstract string EngineDisplayName { get; }

    /// <summary>The configured migration mode.</summary>
    protected GovernanceMigrationMode Mode => _options.Mode;

    /// <summary>
    /// Label of the database for the drift message (SEC-94-08g): the SQLite file name or the database name, never a connection
    /// string, host, user or directory path.
    /// </summary>
    protected virtual string DatabaseLabel(DbConnection connection) => SanitizeLabel(connection.Database);

    internal static string SanitizeLabel(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "?";
        }

        var chars = value.Take(64).Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '$' or ' ' ? c : '?').ToArray();
        return new string(chars);
    }

    /// <summary>Verifies dialect-specific post-conditions (e.g. the append-only audit trigger exists and is enabled). Runs in Apply and Verify mode and must only read. Throws when violated.</summary>
    protected virtual Task VerifyPostConditionsAsync(DbConnection connection, CancellationToken ct) => Task.CompletedTask;

    /// <summary>False when the whole run is already one transaction (SQLite), so steps must not open their own.</summary>
    protected virtual bool UseStepTransactions => true;

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_options.Mode == GovernanceMigrationMode.Skip)
        {
            _logger?.LogWarning("Governance schema migrations are skipped (GovernanceDb:Migrations:Mode = Skip).");
            return;
        }

        var steps = Steps.OrderBy(s => s.Version).ToArray();
        ValidateCatalogue(steps);

        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await AcquireLockAsync(connection, ct).ConfigureAwait(false);
            var success = false;
            try
            {
                await RunUnderLockAsync(connection, steps, ct).ConfigureAwait(false);
                success = true;
            }
            finally
            {
                try
                {
                    await ReleaseLockAsync(connection, success, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (!success)
                {
                    // Do not mask the migration failure that is already propagating.
                    _logger?.LogError(ex, "Releasing the governance schema lock failed after a failed migration run.");
                }
            }
        }
        finally
        {
            if (OwnsConnection)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RunUnderLockAsync(DbConnection connection, MigrationStep[] steps, CancellationToken ct)
    {
        var verifyOnly = _options.Mode == GovernanceMigrationMode.Verify;
        if (!verifyOnly)
        {
            await ExecuteAsync(connection, null, CreateVersionTableSql, ct).ConfigureAwait(false);
        }

        // Re-read under the lock: another replica may have applied steps while this one waited.
        var applied = await ReadAppliedAsync(connection, verifyOnly, ct).ConfigureAwait(false);
        var knownMax = steps.Length == 0 ? 0 : steps[^1].Version;

        if (!_options.AllowNewerSchema && applied.Count > 0 && applied.Keys.Max() > knownMax)
        {
            throw new GovernanceSchemaDriftException(
                $"The governance database contains migration version {applied.Keys.Max()} but this build only knows up to {knownMax} " +
                "(database newer than binary). Deploy a matching build or set GovernanceDb:Migrations:AllowNewerSchema=true.");
        }

        foreach (var step in steps)
        {
            if (applied.TryGetValue(step.Version, out var storedChecksum) &&
                !string.Equals(storedChecksum, step.Checksum, StringComparison.Ordinal))
            {
                throw new GovernanceSchemaDriftException(
                    $"Governance schema drift: migration {step.Version:D4} '{step.Name}' was applied with checksum '{storedChecksum}' but this build expects '{step.Checksum}'.");
            }
        }

        var pending = steps.Where(s => !applied.ContainsKey(s.Version)).ToArray();
        if (verifyOnly)
        {
            if (pending.Length > 0)
            {
                throw new GovernanceSchemaDriftException(
                    $"Governance schema is not up to date (Mode = Verify): pending migrations {string.Join(", ", pending.Select(p => p.Version.ToString("D4", CultureInfo.InvariantCulture)))}.");
            }

            await VerifyShapeAsync(connection, applied, steps, ct).ConfigureAwait(false);
        }
        else
        {
            for (var i = 0; i < pending.Length; i++)
            {
                var step = pending[i];
                ct.ThrowIfCancellationRequested();
                _logger?.LogInformation("Applying governance migration {Version} '{Name}'.", step.Version, step.Name);
                // The shape is verified after the last pending step and before its version row is recorded (plan D-1).
                Func<DbTransaction?, Task>? beforeRecord = i == pending.Length - 1 ? stepTx => VerifyShapeAsync(connection, applied, steps, ct, stepTx) : null;
                try
                {
                    await ApplyStepAsync(connection, step, beforeRecord, ct).ConfigureAwait(false);
                }
                catch (DbException ex)
                {
                    // Plan D-4: a failed step on a drifted database is reported with the list of deviations.
                    var drift = await TryDiagnoseAsync(connection, applied, steps, ex, ct).ConfigureAwait(false);
                    if (drift is null)
                    {
                        throw;
                    }

                    throw drift;
                }
            }

            if (pending.Length == 0)
            {
                await VerifyShapeAsync(connection, applied, steps, ct).ConfigureAwait(false);
            }
        }

        await VerifyPostConditionsAsync(connection, ct).ConfigureAwait(false);
    }

    private async Task VerifyShapeAsync(DbConnection connection, Dictionary<int, string> applied, MigrationStep[] steps, CancellationToken ct, DbTransaction? transaction = null)
    {
        var result = await ShapeVerifier.VerifyAsync(connection, ct, transaction).ConfigureAwait(false);
        if (result.ExtraTables.Count > 0)
        {
            _logger?.LogInformation(
                "The governance database contains {Count} table(s) that this build does not know: {Tables}. They are tolerated.",
                result.ExtraTables.Count,
                string.Join(", ", result.ExtraTables.Take(20)));
        }

        if (result.Deviations.Count > 0)
        {
            throw BuildDrift(connection, applied, steps, result.Deviations, null);
        }
    }

    private async Task<GovernanceSchemaDriftException?> TryDiagnoseAsync(
        DbConnection connection, Dictionary<int, string> applied, MigrationStep[] steps, DbException failure, CancellationToken ct)
    {
        try
        {
            var result = await ShapeVerifier.VerifyAsync(connection, ct).ConfigureAwait(false);
            return result.Deviations.Count == 0 ? null : BuildDrift(connection, applied, steps, result.Deviations, failure);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The diagnosis is best effort: the original failure stays the reported one.
            _logger?.LogDebug(ex, "The schema shape diagnosis after a failed governance migration step did not complete.");
            return null;
        }
    }

    private GovernanceSchemaDriftException BuildDrift(
        DbConnection connection, Dictionary<int, string> applied, MigrationStep[] steps, IReadOnlyList<SchemaDeviation> deviations, Exception? inner)
    {
        var message = GovernanceSchemaShapeComparer.FormatMessage(
            EngineDisplayName,
            DatabaseLabel(connection),
            applied.Keys.ToArray(),
            steps.Where(s => !applied.ContainsKey(s.Version)).Select(s => s.Version).ToArray(),
            deviations);
        _logger?.LogError("{DriftMessage}", message);
        Activity.Current?.SetTag("autheris.governance.schema.deviations", deviations.Count);
        return new GovernanceSchemaDriftException(message, deviations, inner);
    }

    private async Task ApplyStepAsync(DbConnection connection, MigrationStep step, Func<DbTransaction?, Task>? beforeRecord, CancellationToken ct)
    {
        DbTransaction? tx = null;
        if (step.Transactional && UseStepTransactions)
        {
            tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await step.Apply(new MigrationContext(connection, tx, _logger, ct)).ConfigureAwait(false);
            if (beforeRecord != null)
            {
                await beforeRecord(tx).ConfigureAwait(false);
            }

            await RecordAsync(connection, tx, step, ct).ConfigureAwait(false);
            if (tx != null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            if (tx != null)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Rollback of governance migration {Version} failed.", step.Version);
                }
            }

            throw;
        }
        finally
        {
            if (tx != null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RecordAsync(DbConnection connection, DbTransaction? tx, MigrationStep step, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = InsertVersionSql;
        AddParameter(cmd, "@version", step.Version);
        AddParameter(cmd, "@name", step.Name);
        AddParameter(cmd, "@checksum", step.Checksum);
        AddParameter(cmd, "@applied_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        AddParameter(cmd, "@applied_by", Environment.MachineName + "/" + Environment.UserName);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, string>> ReadAppliedAsync(DbConnection connection, bool tolerateMissingTable, CancellationToken ct)
    {
        var result = new Dictionary<int, string>();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SelectVersionsSql;
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
        catch (DbException) when (tolerateMissingTable)
        {
            // Verify mode on a database that was never migrated: nothing is applied.
            result.Clear();
        }

        return result;
    }

    private static void ValidateCatalogue(MigrationStep[] steps)
    {
        for (var i = 0; i < steps.Length; i++)
        {
            if (steps[i].Version != i + 1)
            {
                throw new InvalidOperationException(
                    $"The governance migration catalogue must be gap-free and start at 1; found version {steps[i].Version} at position {i + 1}.");
            }
        }
    }

    protected static async Task ExecuteAsync(DbConnection connection, DbTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    protected static async Task<object?> ScalarAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
