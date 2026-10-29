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

    /// <summary>Test seam: extra steps appended to the catalogue. Never set in production code (an architecture test pins that).</summary>
    internal IReadOnlyList<MigrationStep> AdditionalSteps { get; init; } = [];

    /// <summary>Test seam: receives <c>ddl:*</c> and <c>apply:N</c> markers right before the run mutates the database.</summary>
    internal Action<string>? Trace { get; init; }

    protected abstract string CreateVersionTableSql { get; }

    /// <summary>Selects <c>version, checksum, name</c> of the applied steps.</summary>
    protected abstract string SelectVersionsSql { get; }

    protected abstract string InsertVersionSql { get; }

    /// <summary>Returns a positive count when the history table exists.</summary>
    protected abstract string HistoryTableExistsSql { get; }

    /// <summary>When false the connection is owned by the caller (SQLite shared governance connection) and not disposed.</summary>
    protected virtual bool OwnsConnection => true;

    /// <summary>Opens the connection; <paramref name="verifyOnly"/> runs with the runtime login, never with the DDL login.</summary>
    protected abstract Task<DbConnection> OpenConnectionAsync(bool verifyOnly, CancellationToken ct);

    /// <summary>Acquires the cross-process schema lock within <paramref name="timeout"/>; it is held for the whole run. Throws <c>GOVERNANCE_MIGRATION_LOCK_TIMEOUT</c> when it cannot.</summary>
    protected abstract Task AcquireLockAsync(DbConnection connection, TimeSpan timeout, CancellationToken ct);

    /// <summary>Releases the schema lock; <paramref name="success"/> tells a transaction-based lock whether to commit.</summary>
    protected abstract Task ReleaseLockAsync(DbConnection connection, bool success, CancellationToken ct);

    /// <summary>The read-only shape verifier of the engine (plan D-1). Runs in Apply and Verify mode, under the schema lock (Apply).</summary>
    internal abstract GovernanceSchemaShapeVerifier ShapeVerifier { get; }

    /// <summary>Engine name used in the drift message.</summary>
    protected abstract string EngineDisplayName { get; }

    /// <summary>The configured migration mode.</summary>
    protected GovernanceMigrationMode Mode => _options.Mode;

    /// <summary>The migration options (timeouts of the step bounds).</summary>
    protected GovernanceMigrationOptions Options => _options;

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

    /// <summary>
    /// Verifies dialect-specific post-conditions (e.g. the append-only audit trigger exists and is enabled; I388-SC-06). Runs in Apply and
    /// Verify mode and inside every step transaction (<paramref name="transaction"/>). Must only read. Throws when violated.
    /// </summary>
    protected virtual Task VerifyPostConditionsAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Applies the timeouts of a step transaction (PostgreSQL <c>SET LOCAL</c>, SQL Server <c>SET LOCK_TIMEOUT</c>/<c>XACT_ABORT</c>; I388-SC-08).</summary>
    protected virtual Task BeginStepBoundsAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Extra checks after a file-based step (SQLite: <c>PRAGMA foreign_key_check</c>).</summary>
    protected virtual Task AfterStepAsync(DbConnection connection, DbTransaction? transaction, MigrationStep step, CancellationToken ct) => Task.CompletedTask;

    /// <summary>False when the whole run is already one transaction (SQLite), so steps must not open their own.</summary>
    protected virtual bool UseStepTransactions => true;

    /// <summary>True in the Production environment (I388-SC-13 runtime-DDL warning applies only there).</summary>
    internal bool IsProduction { get; init; }

    /// <summary>True when an Apply would run DDL with the runtime login (no separate <c>MigrationConnectionString</c>); false for SQLite and for engines with a migration login.</summary>
    protected virtual bool RuntimeLoginRunsDdl => false;

    /// <summary>Test seam (never set in production code): overrides <see cref="RuntimeLoginRunsDdl"/>.</summary>
    internal bool? RuntimeLoginRunsDdlOverride { get; init; }

    /// <summary>Test seam (never set in production code): replaces the engine privilege introspection of Verify.</summary>
    internal Func<string?>? ExcessPrivilegeOverride { get; init; }

    /// <summary>Test seam: receives the pending audit step count instead of the repository (never set in production code).</summary>
    internal Func<int, Task>? RuntimeDdlAuditOverride { get; init; }

    private bool RunsDdlWithRuntimeLogin => RuntimeLoginRunsDdlOverride ?? RuntimeLoginRunsDdl;

    /// <summary>
    /// Verify mode, runtime login: describes a privilege that exceeds least privilege (write on the history table, DDL rights), or null
    /// (I388-SC-13, plan Q-2). Must only read. SQLite does not implement it.
    /// </summary>
    protected virtual Task<string?> DescribeExcessPrivilegeAsync(DbConnection connection, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Best-effort audit event for <c>GOVERNANCE_MIGRATION_RUNTIME_DDL</c>; only called after the store is initialised.</summary>
    protected virtual Task RecordRuntimeDdlAuditAsync(int appliedSteps, CancellationToken ct) => Task.CompletedTask;

    private int _pendingRuntimeDdlAuditSteps;

    /// <summary>Emits the pending runtime-DDL audit event once the audit store is ready. A failure is logged and never blocks startup (plan Q-1).</summary>
    public async Task EmitPendingAuditAsync(CancellationToken ct = default)
    {
        var steps = Interlocked.Exchange(ref _pendingRuntimeDdlAuditSteps, 0);
        if (steps == 0)
        {
            return;
        }

        try
        {
            await (RuntimeDdlAuditOverride?.Invoke(steps) ?? RecordRuntimeDdlAuditAsync(steps, ct)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "{Code}: the audit event could not be recorded (best effort, startup continues).", GovernanceMigrationCodes.RuntimeDdl);
        }
    }

    public Task RunAsync(CancellationToken ct = default) => RunCoreAsync(_options.Mode, ct);

    public Task ApplyAsync(CancellationToken ct = default)
    {
        if (_options.Mode != GovernanceMigrationMode.Apply)
        {
            _logger?.LogInformation(
                "GovernanceDb:Migrations:Mode = {Mode} is ignored by the explicit governance migration run: applying the pending migrations.", _options.Mode);
        }

        return RunCoreAsync(GovernanceMigrationMode.Apply, ct);
    }

    public Task VerifyAsync(CancellationToken ct = default) => RunCoreAsync(GovernanceMigrationMode.Verify, ct);

    private MigrationStep[] EffectiveSteps() => Steps.Concat(AdditionalSteps).OrderBy(s => s.Version).ToArray();

    private async Task RunCoreAsync(GovernanceMigrationMode mode, CancellationToken ct)
    {
        if (mode == GovernanceMigrationMode.Skip)
        {
            _logger?.LogWarning("Governance schema migrations are skipped (GovernanceDb:Migrations:Mode = Skip).");
            return;
        }

        var steps = EffectiveSteps();
        ValidateCatalogue(steps);
        var verifyOnly = mode == GovernanceMigrationMode.Verify;
        if (!verifyOnly && IsProduction && RunsDdlWithRuntimeLogin)
        {
            _logger?.LogWarning(
                "{Code}: the governance migration runs DDL with the runtime login because GovernanceDb:MigrationConnectionString is not set; a separate migration login with DDL rights is recommended.",
                GovernanceMigrationCodes.RuntimeDdl);
        }

        var timeout = TimeSpan.FromSeconds(_options.LockTimeoutSeconds);

        DbConnection? connection = null;
        try
        {
            var locked = false;
            using (var acquire = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (!verifyOnly)
                {
                    acquire.CancelAfter(timeout);
                }

                try
                {
                    connection = await OpenConnectionAsync(verifyOnly, acquire.Token).ConfigureAwait(false);
                    if (!verifyOnly)
                    {
                        await AcquireLockAsync(connection, timeout, acquire.Token).ConfigureAwait(false);
                        locked = true;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !verifyOnly)
                {
                    throw LockTimeout();
                }
            }

            var success = false;
            try
            {
                await RunUnderLockAsync(connection, steps, verifyOnly, ct).ConfigureAwait(false);
                success = true;
            }
            finally
            {
                if (locked)
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
        }
        finally
        {
            if (connection is not null && OwnsConnection)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private GovernanceSchemaDriftException LockTimeout() => LockTimeoutError(EngineDisplayName, _options.LockTimeoutSeconds);

    /// <summary>Builds the lock-timeout error for the dialect lock implementations.</summary>
    protected GovernanceSchemaDriftException LockTimeoutError() => LockTimeout();

    internal static GovernanceSchemaDriftException LockTimeoutError(string engine, int seconds) =>
        new(GovernanceMigrationCodes.LockTimeout,
            $"The governance schema lock on the {engine} database was not acquired within {seconds} seconds (fail closed; another migrator holds it).");

    private sealed record AppliedRow(int Version, string Checksum, string Name);

    private async Task RunUnderLockAsync(DbConnection connection, MigrationStep[] steps, bool verifyOnly, CancellationToken ct)
    {
        // Re-read under the lock: another replica may have applied steps while this one waited.
        var historyExists = await HistoryTableExistsAsync(connection, ct).ConfigureAwait(false);
        var applied = historyExists ? await ReadAppliedAsync(connection, ct).ConfigureAwait(false) : [];
        var knownMax = steps.Length == 0 ? 0 : steps[^1].Version;
        var maxApplied = applied.Count == 0 ? 0 : applied.Max(a => a.Version);

        CheckHistory(applied, steps, knownMax, maxApplied);

        var appliedVersions = applied.Select(a => a.Version).ToHashSet();
        var pending = steps.Where(s => !appliedVersions.Contains(s.Version)).ToArray();

        if (verifyOnly)
        {
            if (pending.Length > 0)
            {
                throw new GovernanceSchemaDriftException(
                    GovernanceMigrationCodes.Pending,
                    $"Governance schema is not up to date (Mode = Verify): pending migrations {string.Join(", ", pending.Select(p => p.Version.ToString("D4", CultureInfo.InvariantCulture)))}.");
            }

            await EnsureShapeAsync(connection, steps, Math.Min(maxApplied, knownMax), appliedVersions, pending, GovernanceMigrationCodes.Drift, ct).ConfigureAwait(false);
            await VerifyPostConditionsAsync(connection, null, ct).ConfigureAwait(false);
            await CheckLeastPrivilegeAsync(connection, ct).ConfigureAwait(false);
            return;
        }

        if (maxApplied > knownMax)
        {
            // AllowNewerSchema: the known prefix is still checked; no DDL runs when newer rows exist (I388-SC-10).
            await EnsureShapeAsync(connection, steps, knownMax, appliedVersions, pending, GovernanceMigrationCodes.Drift, ct).ConfigureAwait(false);
            await VerifyPostConditionsAsync(connection, null, ct).ConfigureAwait(false);
            return;
        }

        // Nothing is mutated before the existing schema is accepted (I388-SC-04).
        if (applied.Count == 0)
        {
            await AdoptionCheckAsync(connection, steps, ct).ConfigureAwait(false);
        }
        else
        {
            await EnsureShapeAsync(
                connection, steps, maxApplied, appliedVersions, pending,
                maxApplied == 1 ? GovernanceMigrationCodes.UnsupportedBase : GovernanceMigrationCodes.Drift, ct).ConfigureAwait(false);
        }

        if (!historyExists)
        {
            Trace?.Invoke("ddl:create-history");
            await ExecuteAsync(connection, null, CreateVersionTableSql, ct).ConfigureAwait(false);
        }

        foreach (var step in pending)
        {
            ct.ThrowIfCancellationRequested();
            _logger?.LogInformation("Applying governance migration {Version} '{Name}'.", step.Version, step.Name);
            try
            {
                await ApplyStepAsync(connection, step, steps, appliedVersions, pending, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not GovernanceSchemaDriftException and not OperationCanceledException)
            {
                throw await StepFailureAsync(connection, step, steps, appliedVersions, pending, ex, ct).ConfigureAwait(false);
            }

            appliedVersions.Add(step.Version);
        }

        await VerifyPostConditionsAsync(connection, null, ct).ConfigureAwait(false);
        if (pending.Length > 0 && IsProduction && RunsDdlWithRuntimeLogin)
        {
            Interlocked.Exchange(ref _pendingRuntimeDdlAuditSteps, pending.Length);
        }
    }

    private async Task CheckLeastPrivilegeAsync(DbConnection connection, CancellationToken ct)
    {
        var excess = ExcessPrivilegeOverride is { } probe ? probe() : await DescribeExcessPrivilegeAsync(connection, ct).ConfigureAwait(false);
        if (excess is null)
        {
            return;
        }

        if (_options.RequireLeastPrivilege)
        {
            throw new GovernanceSchemaDriftException(
                GovernanceMigrationCodes.RuntimeLoginPrivileged,
                $"The runtime login is more privileged than needed ({excess}); GovernanceDb:Migrations:RequireLeastPrivilege is set (fail closed).");
        }

        _logger?.LogWarning(
            "{Code}: the runtime login is more privileged than needed ({Excess}). Use a separate migration login (GovernanceDb:MigrationConnectionString) and revoke DDL and history-table write rights from the runtime login.",
            GovernanceMigrationCodes.RuntimeLoginPrivileged, excess);
    }

    private void CheckHistory(List<AppliedRow> applied, MigrationStep[] steps, int knownMax, int maxApplied)
    {
        if (applied.GroupBy(a => a.Version).Any(g => g.Count() > 1))
        {
            throw new GovernanceSchemaDriftException(GovernanceMigrationCodes.HistoryDuplicate, "The migration history contains a version more than once.");
        }

        var versions = applied.Select(a => a.Version).Order().ToArray();
        for (var i = 0; i < versions.Length; i++)
        {
            if (versions[i] != i + 1)
            {
                throw new GovernanceSchemaDriftException(
                    GovernanceMigrationCodes.HistoryGap,
                    $"The migration history has a gap: version {i + 1:D4} is missing but {versions[i]:D4} is applied (applied versions must be exactly 1..k).");
            }
        }

        foreach (var row in applied.Where(a => a.Version <= knownMax))
        {
            var step = steps[row.Version - 1];
            if (!string.Equals(row.Name, step.Name, StringComparison.Ordinal))
            {
                throw new GovernanceSchemaDriftException(
                    GovernanceMigrationCodes.HistoryRenamed,
                    $"Migration {row.Version:D4} was recorded as '{row.Name}' but this build names it '{step.Name}'.");
            }
        }

        if (maxApplied > knownMax)
        {
            if (!_options.AllowNewerSchema)
            {
                throw new GovernanceSchemaDriftException(
                    GovernanceMigrationCodes.NewerThanBuild,
                    $"The governance database contains migration version {maxApplied} but this build only knows up to {knownMax} " +
                    "(database newer than binary). Deploy a matching build or set GovernanceDb:Migrations:AllowNewerSchema=true.");
            }

            _logger?.LogWarning(
                "{Code}: the governance database is at migration {Applied} but this build only knows up to {Known}; no DDL runs and only the known prefix is checked (AllowNewerSchema).",
                GovernanceMigrationCodes.NewerAllowedWarning, maxApplied, knownMax);
        }

        foreach (var row in applied.Where(a => a.Version <= knownMax))
        {
            var step = steps[row.Version - 1];
            if (!string.Equals(row.Checksum, step.Checksum, StringComparison.Ordinal))
            {
                throw new GovernanceSchemaDriftException(
                    GovernanceMigrationCodes.ChecksumMismatch,
                    $"Governance schema drift: migration {step.Version:D4} '{step.Name}' was applied with checksum '{row.Checksum}' but this build expects '{step.Checksum}'.");
            }
        }
    }

    /// <summary>The expected shape at <paramref name="version"/> (v0001 folded with the step deltas), plus the history table when it exists.</summary>
    private GovernanceSchemaShape ExpectedAt(MigrationStep[] steps, int version, bool withHistory)
    {
        var deltas = new Dictionary<int, GovernanceSchemaShapeDelta>();
        foreach (var step in steps.Where(s => s.Version >= 2 && s.Version <= version))
        {
            deltas[step.Version] = step.ShapeDelta
                ?? throw new InvalidOperationException($"Migration {step.Version:D4} '{step.Name}' has no shape delta (fail closed).");
        }

        var shape = GovernanceSchemaShapeLoader.LoadAt(ShapeVerifier.Expected.Engine, Math.Max(version, 1), deltas);
        return withHistory ? shape with { Tables = [.. shape.Tables, HistoryTableShape] } : shape;
    }

    /// <summary>The history table is part of the shape (I388-SC-05): names, type families, nullability and the primary key on <c>version</c>.</summary>
    private static readonly SchemaTable HistoryTableShape = new(
        SchemaCatalogReaderSupport.MigrationTable,
        [
            new SchemaColumn("version", "INTEGER", SchemaTypeFamily.Integer),
            new SchemaColumn("name", "TEXT", SchemaTypeFamily.String),
            new SchemaColumn("checksum", "TEXT", SchemaTypeFamily.String),
            new SchemaColumn("applied_at", "TEXT", SchemaTypeFamily.String),
            new SchemaColumn("applied_by", "TEXT", SchemaTypeFamily.String)
        ],
        ["version"],
        []);

    private async Task EnsureShapeAsync(
        DbConnection connection, MigrationStep[] steps, int version, ISet<int> applied, MigrationStep[] pending, string code, CancellationToken ct)
    {
        var expected = ExpectedAt(steps, version, withHistory: true);
        var result = await ShapeVerifier.VerifyAsync(connection, expected, ct).ConfigureAwait(false);
        LogExtraTables(result);
        if (result.Deviations.Count > 0)
        {
            throw BuildDrift(connection, code, applied, pending, result.Deviations, null);
        }
    }

    /// <summary>Adoption of a database without history: existing governance tables must match v0001 (missing tables and indexes are a partial baseline and allowed).</summary>
    private async Task AdoptionCheckAsync(DbConnection connection, MigrationStep[] steps, CancellationToken ct)
    {
        var result = await ShapeVerifier.VerifyAsync(connection, ExpectedAt(steps, 1, withHistory: false), ct).ConfigureAwait(false);
        var blocking = result.Deviations.Where(d => d.Kind is not (SchemaDeviationKind.MissingTable or SchemaDeviationKind.MissingIndex)).ToArray();
        if (blocking.Length > 0)
        {
            throw BuildDrift(connection, GovernanceMigrationCodes.UnsupportedBase, new HashSet<int>(), steps, blocking, null);
        }
    }

    private void LogExtraTables(SchemaShapeResult result)
    {
        if (result.ExtraTables.Count > 0)
        {
            _logger?.LogInformation(
                "The governance database contains {Count} table(s) that this build does not know: {Tables}. They are tolerated.",
                result.ExtraTables.Count,
                string.Join(", ", result.ExtraTables.Take(20)));
        }
    }

    private GovernanceSchemaDriftException BuildDrift(
        DbConnection connection, string code, ISet<int> applied, IReadOnlyCollection<MigrationStep> pending, IReadOnlyList<SchemaDeviation> deviations, Exception? inner)
    {
        var message = GovernanceSchemaShapeComparer.FormatMessage(
            EngineDisplayName,
            DatabaseLabel(connection),
            applied.ToArray(),
            pending.Where(s => !applied.Contains(s.Version)).Select(s => s.Version).ToArray(),
            deviations);
        _logger?.LogError("{Code}: {DriftMessage}", code, message);
        Activity.Current?.SetTag("autheris.governance.schema.deviations", deviations.Count);
        return new GovernanceSchemaDriftException(code, message, deviations, inner);
    }

    /// <summary>
    /// A failed step is reported with its governed code and a driver error number only (I388-SC-12): never SQL text, row values or
    /// connection details. When the schema drifted, the deviation list is reported instead (plan D-3).
    /// </summary>
    private async Task<GovernanceSchemaDriftException> StepFailureAsync(
        DbConnection connection, MigrationStep step, MigrationStep[] steps, ISet<int> applied, MigrationStep[] pending, Exception failure, CancellationToken ct)
    {
        if (UseStepTransactions)
        {
            try
            {
                var expected = ExpectedAt(steps, step.Version - 1, withHistory: true);
                var result = await ShapeVerifier.VerifyAsync(connection, expected, ct).ConfigureAwait(false);
                if (result.Deviations.Count > 0)
                {
                    return BuildDrift(connection, GovernanceMigrationCodes.Drift, applied, pending, result.Deviations, null);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The diagnosis is best effort: the step failure stays the reported one.
                _logger?.LogDebug("The schema shape diagnosis after a failed governance migration step did not complete ({Type}).", ex.GetType().Name);
            }
        }

        var detail = DescribeFailure(failure);
        _logger?.LogError(
            "{Code}: governance migration {Version} '{Name}' failed on {Engine} ({Detail}); the step was rolled back.",
            GovernanceMigrationCodes.StepFailed, step.Version, step.Name, EngineDisplayName, detail);
        return new GovernanceSchemaDriftException(
            GovernanceMigrationCodes.StepFailed,
            $"Governance migration {step.Version:D4} '{step.Name}' failed on {EngineDisplayName} ({detail}); the step was rolled back. " +
            "Check that the migration login has DDL rights (GovernanceDb:MigrationConnectionString) and see the operations runbook.");
    }

    /// <summary>SQLSTATE or driver error number only; never the exception message (it can carry row values).</summary>
    internal static string DescribeFailure(Exception failure) => failure switch
    {
        DbException db when !string.IsNullOrEmpty(db.SqlState) => "SQLSTATE " + SanitizeLabel(db.SqlState),
        Microsoft.Data.SqlClient.SqlException sql => "error " + sql.Number.ToString(CultureInfo.InvariantCulture),
        Microsoft.Data.Sqlite.SqliteException lite => "SQLite error " + lite.SqliteErrorCode.ToString(CultureInfo.InvariantCulture),
        DbException db => "database error " + db.ErrorCode.ToString(CultureInfo.InvariantCulture),
        _ => failure.GetType().Name
    };

    private async Task ApplyStepAsync(
        DbConnection connection, MigrationStep step, MigrationStep[] steps, ISet<int> applied, MigrationStep[] pending, CancellationToken ct)
    {
        DbTransaction? tx = null;
        if (step.Transactional && UseStepTransactions)
        {
            tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        }

        try
        {
            if (tx is not null && step.Version >= 2)
            {
                await BeginStepBoundsAsync(connection, tx, ct).ConfigureAwait(false);
            }

            Trace?.Invoke("apply:" + step.Version.ToString(CultureInfo.InvariantCulture));
            await step.Apply(new MigrationContext(connection, tx, _logger, ct)).ConfigureAwait(false);

            // The shape of this version and the audit post-conditions are checked inside the step before its history row (I388-SC-06, plan D-5 b).
            var result = await ShapeVerifier.VerifyAsync(connection, ExpectedAt(steps, step.Version, withHistory: true), ct, tx).ConfigureAwait(false);
            LogExtraTables(result);
            if (result.Deviations.Count > 0)
            {
                throw BuildDrift(connection, GovernanceMigrationCodes.Drift, applied, pending, result.Deviations, null);
            }

            await VerifyPostConditionsAsync(connection, tx, ct).ConfigureAwait(false);
            await AfterStepAsync(connection, tx, step, ct).ConfigureAwait(false);
            await RecordAsync(connection, tx, step, ct).ConfigureAwait(false);
            if (tx is not null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            if (tx is not null)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogError("Rollback of governance migration {Version} failed ({Type}).", step.Version, ex.GetType().Name);
                }
            }

            throw;
        }
        finally
        {
            if (tx is not null)
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

    private async Task<bool> HistoryTableExistsAsync(DbConnection connection, CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync(connection, HistoryTableExistsSql, ct).ConfigureAwait(false) ?? 0L, CultureInfo.InvariantCulture) > 0;

    private async Task<List<AppliedRow>> ReadAppliedAsync(DbConnection connection, CancellationToken ct)
    {
        var result = new List<AppliedRow>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectVersionsSql;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new AppliedRow(
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty));
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

    protected static async Task ExecuteAsync(DbConnection connection, DbTransaction? tx, string sql, CancellationToken ct, int? commandTimeoutSeconds = null)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        if (commandTimeoutSeconds is { } timeout)
        {
            cmd.CommandTimeout = timeout;
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    protected static async Task<object?> ScalarAsync(DbConnection connection, string sql, CancellationToken ct, DbTransaction? tx = null)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
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
