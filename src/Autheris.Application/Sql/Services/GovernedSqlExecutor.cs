namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Synthetic;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// AR-05: Dedicated execution engine for governed WebSQL queries and DML statements.
/// Handles connection management, session initialization, concurrency gating, audit recording, and result buffering.
/// X1.2: the only text that reaches a command is <see cref="CompiledSql.Sql"/>, bound through <see cref="CompiledSqlBinders"/>;
/// governed DML runs through the checked executor inside the session transaction; provider errors are reduced to stable codes.
/// </summary>
public sealed class GovernedSqlExecutor
{
    private const string InternalParameterPrefix = GovernedSqlExecutionService.InternalParameterPrefix;
    // SEC-RF107-07(e): the parameters of a substituted row-filter key-set subquery; user SQL and request parameters cannot use the prefix.
    private const string KeySetParameterPrefix = "__rls_ks";
    private const int ThrottledRetryAfterSeconds = 2;

    private readonly GovernedSqlRewriter _rewriter;
    private readonly IOptions<GatewayOptions> _options;
    private readonly IAuditWriter _auditWriter;
    private readonly ISqlConnectionFactory? _connectionFactory;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger? _logger;
    private readonly ITableReadConcurrencyGate? _concurrencyGate;
    private readonly IDbSessionContextInitializer _sessionInitializer;
    private readonly SqlDataSourceResolver _dataSourceResolver;
    private readonly CompiledSqlAuditDigester _digester;
    private readonly IProviderExecutionGuard _executionGuard;

    public GovernedSqlExecutor(
        GovernedSqlRewriter rewriter,
        IOptions<GatewayOptions> options,
        IAuditWriter auditWriter,
        IProviderExecutionGuard executionGuard,
        ISqlConnectionFactory? connectionFactory = null,
        IHostEnvironment? environment = null,
        ILogger? logger = null,
        ITableReadConcurrencyGate? concurrencyGate = null,
        IDbSessionContextInitializer? sessionInitializer = null,
        IKeyVaultSecretProvider? secretProvider = null,
        CompiledSqlAuditDigester? auditDigester = null)
    {
        _rewriter = rewriter ?? throw new ArgumentNullException(nameof(rewriter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _connectionFactory = connectionFactory;
        _environment = environment;
        _logger = logger;
        _concurrencyGate = concurrencyGate;
        _sessionInitializer = sessionInitializer ?? new DbSessionContextInitializer();
        _dataSourceResolver = new SqlDataSourceResolver(_options, logger);
        // L-A1: the host registers one singleton (one key read); only unit-test construction builds its own.
        _digester = auditDigester ?? new CompiledSqlAuditDigester(_options, secretProvider, environment);
        // L-A3: the guard is a required dependency; there is no silent pass-through fallback in the executor.
        _executionGuard = executionGuard ?? throw new ArgumentNullException(nameof(executionGuard));
    }

    public async Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct = default)
    {
        await ExecuteCoreAsync(request, user, tenantId, rowWriter, ct).ConfigureAwait(false);
    }

    public async Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var columns = new List<string>();
        IReadOnlyList<SqlResultColumn> columnDescriptions = Array.Empty<SqlResultColumn>();
        bool isTruncated = false;
        var sw = Stopwatch.StartNew();

        await ExecuteCoreAsync(
            request,
            user,
            tenantId,
            async (reader, token) =>
            {
                columnDescriptions = SqlResultColumns.Describe(reader);
                foreach (var column in columnDescriptions)
                {
                    columns.Add(column.Name);
                }

                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    rows.Add(row);
                }

                isTruncated = reader is RowLimitedDataReader { HasMoreRows: true };
            },
            ct).ConfigureAwait(false);

        sw.Stop();

        return new GovernedSqlResult(
            OriginalSql: request.Sql,
            Columns: columns.AsReadOnly(),
            Rows: rows.AsReadOnly(),
            RowCount: rows.Count,
            ElapsedMilliseconds: sw.ElapsedMilliseconds,
            Truncated: isTruncated,
            ColumnDescriptions: columnDescriptions,
            InDbColumnMaskingExecuted: !_options.Value.IsColumnMaskingDisabled);
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRowsAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channel = System.Threading.Channels.Channel.CreateBounded<IReadOnlyDictionary<string, object?>>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });

        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producerToken = producerCts.Token;

        var executionTask = Task.Run(async () =>
        {
            try
            {
                await ExecuteCoreAsync(
                    request,
                    user,
                    tenantId,
                    async (reader, token) =>
                    {
                        var columnDescriptions = SqlResultColumns.Describe(reader);
                        var columns = new string[columnDescriptions.Count];
                        for (int i = 0; i < columnDescriptions.Count; i++)
                        {
                            columns[i] = columnDescriptions[i].Name;
                        }

                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                            }

                            await channel.Writer.WriteAsync(row, token).ConfigureAwait(false);
                        }
                    },
                    producerToken).ConfigureAwait(false);

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        }, CancellationToken.None);

        bool completedEnumerating = false;
        try
        {
            await foreach (var row in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return row;
            }
            completedEnumerating = true;
        }
        finally
        {
            if (!completedEnumerating)
            {
                await producerCts.CancelAsync().ConfigureAwait(false);
            }
        }

        await executionTask.ConfigureAwait(false);
    }

    private async Task<string> ExecuteCoreAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rowWriter);

        string? requestedDs = !string.IsNullOrWhiteSpace(request.DataSourceName)
            ? _dataSourceResolver.ResolveAllowedDataSource(request.DataSourceName, tenantId)
            : null;

        if (request.Parameters != null)
        {
            foreach (var paramName in request.Parameters.Keys)
            {
                var trimmed = paramName.TrimStart('@');
                if (trimmed.StartsWith(InternalParameterPrefix, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("p_rls_", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith(KeySetParameterPrefix, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("rls_", StringComparison.OrdinalIgnoreCase))
                {
                    throw new WebSqlPolicyException("A request parameter uses a reserved gateway parameter name.");
                }
            }
        }

        // Scalar values only (an array or object is a 400). The binder reads a client value only by the name the statement uses.
        var clientValues = BuildClientValues(request.Parameters);
        string sqlForRewrite = GovernedSqlExecutionService.NormalizeClientParameters(request.Sql, request.Parameters, out _);

        var dmlContext = new DmlAuditContext();
        GovernedRewrite rewrite;
        try
        {
            rewrite = await _rewriter.RewriteCoreAsync(sqlForRewrite, user, tenantId, requestedDs, dmlContext, ct, request.RowLimit, probeExtraRow: true, plannedTableRead: request.PlannedTableRead).ConfigureAwait(false);
        }
        catch (Exception policyEx) when (IsAuditedRejection(policyEx, ct))
        {
            string auditDs = requestedDs ?? _options.Value.WebSql.DefaultDataSourceName;
            // SEC-ADG-23: only the gateway's own policy messages are stored; an engine exception is reduced to its stable code.
            // L-4: the same holds for the engine errors that the error mapper owns (unsupported construct, reserved identifier, syntax
            // error, limits): the stable code is stored, never the message, and the exception is rethrown for the error surface to map.
            string reason = DeniedReason(policyEx);
            if (dmlContext.IsDml)
            {
                await RecordDmlAuditAsync(tenantId, user, auditDs, dmlContext, AuditEventTypes.WebSqlDmlRejected, "DENY", compiled: null, affectedRows: null, reason: reason, synthetic: false, originalSql: request.Sql).ConfigureAwait(false);
            }
            else
            {
                var (redactedOriginalSql, originalSqlHash) = _digester.Anonymize(request.Sql);
                var userSid = user.GetUserSid() ?? new Sid("anonymous");
                var detailsJson = new AuditDetailsBuilder()
                    .WithField("originalSql", redactedOriginalSql)
                    .WithField("originalSqlHash", originalSqlHash)
                    .WithField("dataSource", auditDs)
                    .WithField("reasonCode", policyEx switch
                    {
                        WebSqlPolicyException => "POLICY_VIOLATION",
                        SecurityException => "SECURITY_VIOLATION",
                        _ => reason
                    })
                    .WithField("error", reason)
                    .Build();

                await _auditWriter.RecordAsync(new AuditLogEntry
                {
                    TenantId = tenantId,
                    EventType = AuditEventTypes.WebSqlQueryDenied,
                    ActorSid = userSid,
                    TargetTable = auditDs,
                    Decision = "DENY",
                    TraceId = TraceContextResolver.GetCurrentTraceId(),
                    DetailsJson = detailsJson
                }, CancellationToken.None).ConfigureAwait(false);   // a DENY record never depends on the request token
            }
            throw;
        }

        return await ExecuteRewrittenAsync(request, user, tenantId, rewrite, dmlContext, clientValues, rowWriter, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything after a successful compile. Separate (and internal) so that the dialect agreement and the fail-closed rules of the
    /// execution half are testable without a rewriter.
    /// </summary>
    internal async Task<string> ExecuteRewrittenAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        GovernedRewrite rewrite,
        DmlAuditContext dmlContext,
        IReadOnlyDictionary<string, object?> clientValues,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct)
    {
        var compiled = rewrite.Compiled;
        string dsName = rewrite.DataSourceName;

        if (!dmlContext.IsDml)
        {
            var (redactedOriginalSql, originalSqlHash) = _digester.Anonymize(request.Sql);
            var userSid = user.GetUserSid() ?? new Sid("anonymous");
            var fields = new Dictionary<string, object?>
            {
                ["originalSql"] = redactedOriginalSql,
                ["originalSqlHash"] = originalSqlHash,
                ["dataSource"] = dsName,
                ["virtual_filters"] = rewrite.VirtualFilters
            };
            AddCompiledAuditFields(fields, compiled);
            await _auditWriter.RecordAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = AuditEventTypes.WebSqlQuery,
                ActorSid = userSid,
                TargetTable = dsName,
                Decision = "ALLOW",
                TraceId = TraceContextResolver.GetCurrentTraceId(),
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(fields)
            }, ct).ConfigureAwait(false);
        }

        var connOptions = _dataSourceResolver.ResolveConnectionOptions(dsName);

        if (connOptions == null || string.IsNullOrWhiteSpace(connOptions.ConnectionString) || _connectionFactory == null)
        {
            bool isDev = _environment != null &&
                         string.Equals(_environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
            bool isExplicitlyAllowed = _options.Value.AreExternalSystemsMockedIfUnreachable;

            if (!isDev && !isExplicitlyAllowed)
            {
                throw new GatewayNotImplementedException($"No active database connection configured for data source '{dsName}'. Synthetic fallback is disabled outside Development.");
            }

            if (dmlContext.IsDml)
            {
                await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, AuditEventTypes.WebSqlDmlExecuted, "ALLOW", compiled, affectedRows: 0, reason: null, synthetic: true).ConfigureAwait(false);
            }

            // G-11: only after a successful Compile, and only the compiled text (markers, never a bound value).
            using var syntheticReader = new SyntheticDataTableReader(compiled.Sql);
            await rowWriter(syntheticReader, ct).ConfigureAwait(false);
            return compiled.Sql;
        }

        // SEC-X1-14: the binder dialect comes from the connection provider and must be the dialect the statement was compiled for.
        // An unmapped provider no longer skips the session initializer silently: it fails closed before a connection or command exists.
        if (!DataSourceProvider.TryResolveDialect(connOptions.Provider, out var connectionDialect) ||
            !SqlDialectMapper.TryToTargetDialect(connectionDialect, out var binderDialect) ||
            binderDialect != compiled.Dialect)
        {
            _logger?.LogError(
                "WebSQL refused to execute: the provider of data source {DataSource} does not map to the dialect {CompiledDialect} the statement was compiled for.",
                dsName, compiled.Dialect);
            throw new GovernedSqlException(GovernedSqlErrorCodes.CompilerInvariant, compiled.Dialect);
        }

        var binder = CompiledSqlBinders.For(binderDialect);

        if (dmlContext.IsDml)
        {
            // L-2: the audit key is resolved before the statement can write. A key that cannot be read fails the statement closed here,
            // not after the commit (a committed write without its audit record).
            _digester.EnsureKey();
        }

        var maxConcurrentReads = _options.Value.DataSources?.MaxConcurrentReadsPerUserAndTable ?? 0;
        List<IDisposable>? leases = null;
        if (_concurrencyGate != null && maxConcurrentReads > 0 && rewrite.AccessedTables.Count > 0)
        {
            var userSid = user.FindFirst(ClaimTypes.PrimarySid)?.Value
                          ?? user.FindFirst("sub")?.Value
                          ?? "anonymous";
            leases = new List<IDisposable>(rewrite.AccessedTables.Count);
            try
            {
                foreach (var table in rewrite.AccessedTables)
                {
                    var key = $"{tenantId.Value}|{userSid}|{table.ToQualifiedName()}".ToLowerInvariant();
                    var lease = _concurrencyGate.TryEnter(key, maxConcurrentReads)
                                ?? throw new GatewayThrottledException(ThrottledRetryAfterSeconds);
                    leases.Add(lease);
                }
            }
            catch
            {
                foreach (var lease in leases)
                {
                    lease.Dispose();
                }
                throw;
            }
        }

        DbTransaction? tx = null;
        bool txCompleted = false;
        try
        {
            await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);

            var sessionSid = user.FindFirst(ClaimTypes.PrimarySid)?.Value
                             ?? user.FindFirst("sub")?.Value;
            tx = await _sessionInitializer.InitializeSessionAsync(
                connection,
                connectionDialect,
                tenantId,
                userSid: sessionSid,
                purpose: null,
                requireTransaction: true,
                ct: ct).ConfigureAwait(false);

            int commandTimeout = Math.Max(1, _options.Value.WebSql.ExecutionTimeoutSeconds);

            if (dmlContext.IsDml)
            {
                // SEC-X1-06: the session initializer gives no transaction on SQL Server, Oracle and SQLite, so one is begun here. From the
                // call on, the checked executor is the single owner of the completion (commit or rollback); this method only disposes it.
                tx ??= await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                txCompleted = true;

                var limits = new CheckedDmlLimits(commandTimeout, _options.Value.WebSql.MaxAffectedRows);
                int affectedRows;
                try
                {
                    var checkedTx = tx;
                    affectedRows = await _executionGuard.RunAsync(
                        connection,
                        TimeSpan.FromSeconds(commandTimeout),
                        () => CompiledSqlBinders.CheckedExecutorFor(binderDialect).ExecuteInTransactionAsync(checkedTx, compiled, clientValues, limits, ct),
                        ct).ConfigureAwait(false);
                }
                catch (Exception dmlEx)
                {
                    bool rejected = dmlEx is DmlAffectedRowsLimitException or DmlCheckOptionViolationException;
                    try
                    {
                        await RecordDmlAuditAsync(
                            tenantId, user, dsName, dmlContext,
                            rejected ? AuditEventTypes.WebSqlDmlRejected : AuditEventTypes.WebSqlDmlFailed, "DENY", compiled,
                            affectedRows: null, reason: rejected ? ErrorCodeOf(dmlEx) : dmlEx.GetType().Name, synthetic: false).ConfigureAwait(false);
                    }
                    catch (Exception auditEx)
                    {
                        // The statement's own failure is what the caller must see, not a failure of its audit.
                        _logger?.LogError(auditEx, "WebSQL DML failure audit could not be written on data source {DataSource}.", dsName);
                    }

                    throw;
                }

                await RecordCommittedDmlAuditAsync(tenantId, user, dsName, dmlContext, compiled, affectedRows).ConfigureAwait(false);

                using var emptyTable = new DataTable();
                using var emptyReader = emptyTable.CreateDataReader();
                await rowWriter(emptyReader, ct).ConfigureAwait(false);
                return compiled.Sql;
            }

            await using var command = connection.CreateCommand();
            if (tx != null)
            {
                command.Transaction = tx;
            }

            command.CommandTimeout = commandTimeout;
            binder.Bind(command, compiled, clientValues);

            // SEC-X1-10: the execution and the streaming of the rows run under the provider guard (SQLite interrupts at the timeout).
            await _executionGuard.RunAsync(
                connection,
                TimeSpan.FromSeconds(commandTimeout),
                async () =>
                {
                    await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false))
                    {
                        if (rewrite.DeliveredRowLimit > 0)
                        {
                            await rowWriter(new RowLimitedDataReader(reader, rewrite.DeliveredRowLimit), ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await rowWriter(reader, ct).ConfigureAwait(false);
                        }
                    }

                    return true;
                },
                ct).ConfigureAwait(false);

            if (tx != null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                txCompleted = true;
            }
            return compiled.Sql;
        }
        catch (Exception ex)
        {
            // A driver error is reduced to a stable code first; the original is logged server-side only (see SanitizeProviderError).
            var sanitized = SanitizeProviderError(ex, binderDialect, dsName);
            var reported = sanitized ?? ex;

            if (!dmlContext.IsDml)
            {
                var (redactedOriginalSql, originalSqlHash) = _digester.Anonymize(request.Sql);
                var userSid = user.GetUserSid() ?? new Sid("anonymous");
                // SEC-X1-12: the stable code and the exception type, never ex.Message (a driver message can carry key values).
                var detailsJson = new AuditDetailsBuilder()
                    .WithField("originalSql", redactedOriginalSql)
                    .WithField("originalSqlHash", originalSqlHash)
                    .WithField("dataSource", dsName)
                    .WithField("reasonCode", (ex is OperationCanceledException && ct.IsCancellationRequested) ? "CLIENT_ABORTED" : ex.GetType().Name)
                    .WithField("errorCode", ErrorCodeOf(reported))
                    .WithField("errorType", ex.GetType().Name)
                    .Build();

                // Best effort: the statement's own error surface wins over a failing audit store (counted as tier=best_effort).
                await _auditWriter.RecordBestEffortAsync(new AuditLogEntry
                {
                    TenantId = tenantId,
                    EventType = AuditEventTypes.QueryExecutionError,
                    ActorSid = userSid,
                    TargetTable = dsName,
                    Decision = "ERROR",
                    TraceId = TraceContextResolver.GetCurrentTraceId(),
                    DetailsJson = detailsJson
                }, CancellationToken.None).ConfigureAwait(false);
            }

            if (tx != null && !txCompleted)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogWarning(rbEx, "Failed to rollback WebSQL transaction.");
                }
            }

            if (sanitized is not null)
            {
                throw sanitized;
            }

            throw;
        }
        finally
        {
            if (tx != null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
            if (leases != null)
            {
                foreach (var lease in leases)
                {
                    lease.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// A driver error becomes a typed error with a stable code and a fixed message; the original is logged server-side only. A timeout
    /// or an unavailable source is classified first and keeps its own status (504, 503). Everything that is not a driver error
    /// (policy and binder refusals, cancellation, the gateway's own exceptions) passes through unchanged.
    /// </summary>
    private GovernedSqlException? SanitizeProviderError(Exception error, TargetSqlDialect dialect, string dataSourceName)
    {
        if (error is GovernedSqlException || !IsProviderError(error))
        {
            return null;
        }

        if (DataAccessErrorClassifier.Classify(error) is DataAccessErrorKind.Timeout or DataAccessErrorKind.Unavailable)
        {
            return null;
        }

        _logger?.LogWarning(error, "WebSQL provider error on data source {DataSource} ({Dialect}); the caller receives a sanitized error. TraceId={TraceId}",
            dataSourceName, dialect, Activity.Current?.TraceId.ToString());
        return DmlErrorSanitizer.Map(dialect, error);
    }

    private static bool IsProviderError(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is DbException)
            {
                return true;
            }
        }

        return false;
    }

    private static string ErrorCodeOf(Exception error) =>
        SqlCompilerErrorMapper.TryMap(error, out var mapping) ? mapping.Code : "UNMAPPED";

    /// <summary>The audit reason of a rejected statement: the gateway's own policy message, or the stable code of an engine rejection.</summary>
    private static string DeniedReason(Exception error) => error switch
    {
        WebSqlPolicyException => error.Message,
        SecurityException => ErrorCodeOf(error),
        _ => SqlCompilerErrorMapper.TryMap(error, out var mapping) ? mapping.Code : BadRequestCode
    };

    /// <summary>The audit code of an <see cref="ArgumentException"/> that no mapper rule owns (an empty or oversized statement).</summary>
    private const string BadRequestCode = "SQL_BAD_REQUEST";

    /// <summary>
    /// L-4: what the compile half refuses and audits before it rethrows: every security exception, and every exception that the error
    /// mapper owns or that is a bad request (<see cref="ArgumentException"/>). A client that cancelled is not a rejection.
    /// </summary>
    private static bool IsAuditedRejection(Exception error, CancellationToken ct) =>
        error is SecurityException ||
        (!(error is OperationCanceledException && ct.IsCancellationRequested) &&
         (error is ArgumentException || SqlCompilerErrorMapper.TryMap(error, out _)));

    private static IReadOnlyDictionary<string, object?> BuildClientValues(IReadOnlyDictionary<string, object?>? parameters)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (parameters == null)
        {
            return values;
        }

        foreach (var (name, value) in parameters)
        {
            // The first spelling wins when two names differ only by case (as before).
            values.TryAdd(name.TrimStart('@'), WebSqlParameterValues.Normalize(value));
        }

        return values;
    }

    /// <summary>X1.2 / SEC-ADG-23: compiler version, dialect, bind count and the digest; the compiled text only with Audit:StoreCompiledSql.</summary>
    private void AddCompiledAuditFields(Dictionary<string, object?> fields, CompiledSql compiled)
    {
        fields["compiler_version"] = compiled.CompilerVersion;
        fields["dialect"] = compiled.Dialect.ToString();
        fields["bind_count"] = compiled.Parameters.Length;
        fields["sql.compiled_digest"] = _digester.Compute(compiled);
        if (_options.Value.Audit.StoreCompiledSql)
        {
            fields["compiledSql"] = compiled.Sql;
        }
    }

    private async Task RecordDmlAuditAsync(
        TenantId tenantId,
        ClaimsPrincipal user,
        string dsName,
        DmlAuditContext dmlContext,
        string eventType,
        string decision,
        CompiledSql? compiled,
        int? affectedRows,
        string? reason,
        bool synthetic,
        string? originalSql = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["statementType"] = dmlContext.StatementType?.ToString(),
            ["tables"] = dmlContext.Tables,
            ["dataSource"] = dsName,
            ["affectedRows"] = affectedRows,
            ["maxAffectedRows"] = _options.Value.WebSql.MaxAffectedRows,
            ["reason"] = reason,
            ["synthetic"] = synthetic
        };
        if (compiled is not null)
        {
            AddCompiledAuditFields(fields, compiled);
        }
        else if (originalSql is not null)
        {
            // A statement rejected before it compiled has no digest; the hash of its (literal-free) original text identifies it.
            fields["originalSqlHash"] = _digester.Anonymize(originalSql).OriginalSqlHash;
        }

        await _auditWriter.RecordAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = eventType,
            ActorSid = user.GetUserSid() ?? new Sid("anonymous"),
            TargetTable = string.Join(",", dmlContext.Tables),
            Decision = decision,
            TraceId = TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(fields)
            // M-1: a DML audit never takes the request token. The statement may already be committed; a client that disconnects must not
            // leave it without its audit record.
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The audit of a DML that is already committed. An audit failure here is never a request failure (the client would retry and
    /// duplicate the write): it is logged as Critical and counted for the operators.
    /// </summary>
    private async Task RecordCommittedDmlAuditAsync(
        TenantId tenantId, ClaimsPrincipal user, string dsName, DmlAuditContext dmlContext, CompiledSql compiled, int affectedRows)
    {
        try
        {
            await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, AuditEventTypes.WebSqlDmlExecuted, "ALLOW", compiled, affectedRows, reason: null, synthetic: false).ConfigureAwait(false);
        }
        catch (Exception auditEx)
        {
            GatewayDiagnostics.DmlAuditFailedCounter.Add(1);
            _logger?.LogCritical(auditEx,
                "WebSQL DML on data source {DataSource} is committed but its audit record could not be written. TraceId={TraceId}",
                dsName, Activity.Current?.TraceId.ToString());
        }
    }
}
