namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.ListParameters;
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
public sealed partial class GovernedSqlExecutor
{
    private const string InternalParameterPrefix = GovernedSqlExecutionService.InternalParameterPrefix;
    // 07(e): the parameters of a substituted row-filter key-set subquery; user SQL and request parameters cannot use the prefix.
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
    private readonly Autheris.Application.DataSources.IDataSourceTransportGate _transportGate;
    private readonly Autheris.Application.Sql.Paging.IWebSqlPageTokenCodec _pageTokenCodec;

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
        CompiledSqlAuditDigester? auditDigester = null,
        Autheris.Application.DataSources.IDataSourceTransportGate? transportGate = null,
        Autheris.Application.Common.Quota.IQueryCostQuota? costQuota = null,
        Autheris.Application.Sql.Paging.IWebSqlPageTokenCodec? pageTokenCodec = null)
    {
        _costQuota = costQuota;
        // Issue #447 no gate means deny, never pass-through.
        _transportGate = transportGate ?? Autheris.Application.DataSources.TransportGateFallback.Current;
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
        _pageTokenCodec = pageTokenCodec ?? new Autheris.Application.Sql.Paging.WebSqlPageTokenCodec(_digester);
    }

    public async Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, GovernedResultInfo, CancellationToken, Task> rowWriter,
        CancellationToken ct = default)
    {
        await ExecuteCoreAsync(request, user, tenantId, rowWriter, ct).ConfigureAwait(false);
    }

    public Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct = default) =>
        ExecuteGovernedQueryAsync(request, user, tenantId, (reader, _, token) => rowWriter(reader, token), ct);

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

        GovernedSqlPageOutcome? pageOutcome = null;
        if (request.Page != null || request.NextToken != null)
        {
            request = request with { OnPageOutcome = outcome => pageOutcome = outcome };
        }

        await ExecuteCoreAsync(
            request,
            user,
            tenantId,
            async (reader, info, token) =>
            {
                columnDescriptions = info.Columns;
                foreach (var column in columnDescriptions)
                {
                    columns.Add(column.Name);
                }

                var budget = request.ResultByteBudget;
                long totalBytes = 0;
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[columns[i]] = budget is not null
                            ? budget.ReadValue(reader, i, ref totalBytes)
                            : reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    rows.Add(row);
                }

                isTruncated = reader is RowLimitedDataReader { HasMoreRows: true };
            },
            ct).ConfigureAwait(false);

        sw.Stop();

        if (pageOutcome?.Page != null && rows.Count > pageOutcome.Page.Size)
        {
            rows = rows.Take(pageOutcome.Page.Size).ToList();
        }

        return new GovernedSqlResult(
            OriginalSql: request.Sql,
            Columns: columns.AsReadOnly(),
            Rows: rows.AsReadOnly(),
            RowCount: rows.Count,
            ElapsedMilliseconds: sw.ElapsedMilliseconds,
            Truncated: isTruncated,
            ColumnDescriptions: columnDescriptions,
            InDbColumnMaskingExecuted: !_options.Value.IsColumnMaskingDisabled,
            Page: pageOutcome?.Page,
            Next: pageOutcome?.NextToken,
            PagingUnavailable: pageOutcome?.PagingUnavailable);
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
                    async (reader, _, token) =>
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
        Func<DbDataReader, GovernedResultInfo, CancellationToken, Task> rowWriter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rowWriter);

        string? requestedDs = !string.IsNullOrWhiteSpace(request.DataSourceName)
            ? _dataSourceResolver.ResolveAllowedDataSource(request.DataSourceName, tenantId)
            : null;

        GovernedSqlExecutionService.EnsureClientParameterNames(request.Parameters, request.PlannedTableRead);

        var bindNames = GovernedSqlExecutionService.MergeGatewayParameters(request);
        string sqlForRewrite = GovernedSqlExecutionService.NormalizeClientParameters(request.Sql, bindNames, request.GatewayParameters, out var referencedNames);

        if (request.NextToken != null || request.Page != null)
        {
            var meta = FastSqlEngine.Default.Analyze(sqlForRewrite.AsMemory());
            if (meta.StatementType != SqlStatementType.Select)
            {
                throw new SqlPagingUnsupportedException();
            }
        }

        if (request.NextToken != null)
        {
            if (user.GetUserSid() == null)
            {
                throw new SqlPagingTokenInvalidException();
            }

            string sourceId = requestedDs ?? _options.Value.WebSql.DefaultDataSourceName;
            string transport = request.Transport ?? "WebSql";
            long maxOffset = _options.Value.WebSql.Paging?.MaxOffset ?? 10_000;
            var decoded = _pageTokenCodec.DecodeToken(
                request.NextToken,
                request.Sql,
                request.Parameters,
                user,
                tenantId,
                sourceId,
                transport,
                maxOffset);

            if (request.Page != null && request.Page.Size != decoded.Size)
            {
                throw new SqlPagingTokenInvalidException();
            }

            request = request with
            {
                Page = new SqlPageWindow(decoded.Offset, decoded.Size, decoded.PageNumber)
            };
        }

        var dmlContext = new DmlAuditContext();
        GovernedRewrite rewrite;
        IReadOnlyDictionary<string, object?> clientValues;
        try
        {
            // Scalars, and lists on the transports that may carry them; an object is a 400. The binder reads a client
            // value only by the name the statement uses. A list the statement does not reference is dropped here.
            var referenced = referencedNames as IReadOnlySet<string> ?? new HashSet<string>(referencedNames, StringComparer.OrdinalIgnoreCase);
            clientValues = BuildClientValues(request, referenced);
            var shapes = ClientListParameters.Shapes(clientValues, referenced);
            rewrite = await _rewriter.RewriteCoreAsync(sqlForRewrite, user, tenantId, requestedDs, dmlContext, ct, request.RowLimit, probeExtraRow: true, plannedTableRead: request.PlannedTableRead, clientLists: shapes, page: request.Page).ConfigureAwait(false);
        }
        catch (Exception policyEx) when (IsAuditedRejection(policyEx, ct))
        {
            if (policyEx is not (SqlPagingTokenInvalidException or Antlr4.Runtime.Misc.ParseCanceledException or Antlr4.Runtime.RecognitionException))
            {
                await ChargeFailureFeeOnDenialAsync(user).ConfigureAwait(false);
            }
            string auditDs = requestedDs ?? _options.Value.WebSql.DefaultDataSourceName;
            // only the gateway's own policy messages are stored; an engine exception is reduced to its stable code.
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
                var deniedDetails = new AuditDetailsBuilder()
                    .WithField("originalSql", redactedOriginalSql)
                    .WithField("originalSqlHash", originalSqlHash)
                    .WithField("dataSource", auditDs)
                    .WithField("reasonCode", policyEx switch
                    {
                        WebSqlPolicyException => "POLICY_VIOLATION",
                        SecurityException => "SECURITY_VIOLATION",
                        _ => reason
                    })
                    .WithField("error", reason);
                try
                {
                    var meta = FastSqlEngine.Default.Analyze(request.Sql.AsMemory());
                    deniedDetails.WithField("estimatedCost", CalculateEstimatedCost(request, meta));
                }
                catch { }
                if (dmlContext.HasRecursiveCte)
                {
                    deniedDetails.WithField("recursiveCte", true);   // 09
                }

                var detailsJson = deniedDetails.Build();

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

    internal Task<string> ExecuteRewrittenAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        GovernedRewrite rewrite,
        DmlAuditContext dmlContext,
        IReadOnlyDictionary<string, object?> clientValues,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct) =>
        ExecuteRewrittenAsync(request, user, tenantId, rewrite, dmlContext, clientValues, (reader, _, token) => rowWriter(reader, token), ct);

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
        Func<DbDataReader, GovernedResultInfo, CancellationToken, Task> rowWriter,
        CancellationToken ct)
    {
        var compiled = rewrite.Compiled;
        string dsName = rewrite.DataSourceName;

        var admission = request.Admission;
        var executionSw = Stopwatch.StartNew();
        long deliveredOrAffectedRows = 0;
        bool executionFailed = false;

        try
        {
            // Issue #447 C1: the resolved source of the rewrite, before the ALLOW audit record and before any connection exists.
            await _transportGate.EnsureCatalogSourceAsync(dsName, ct).ConfigureAwait(false);

            admission ??= await AdmitCostAsync(request, user, rewrite.Metadata, ct).ConfigureAwait(false);

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
                if (admission != null)
                {
                    fields["cost"] = new Dictionary<string, object?>
                    {
                        ["estimated"] = admission.ReservedCost,
                        ["maxAllowed"] = admission.MaxAllowedCost,
                        ["tier"] = admission.Policy.Tier.ToString()
                    };
                }
                AddCallerAttributeAuditFields(fields, rewrite, compiled);
                AddCompiledAuditFields(fields, compiled, clientValues);
                AddKeySetAuditFields(fields, rewrite);
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
            var synCols = SqlResultColumns.Describe(syntheticReader);
            var synMasks = compiled.OutputColumnMasks;
            if (synMasks.IsDefault || synMasks.Length != syntheticReader.FieldCount)
            {
                synMasks = ImmutableArray.CreateRange(Enumerable.Repeat(false, syntheticReader.FieldCount));
            }
            await rowWriter(syntheticReader, new GovernedResultInfo(synCols, synMasks), ct).ConfigureAwait(false);
            NotifyPageOutcome(request, user, tenantId, rewrite, isTruncated: false);
            return compiled.Sql;
        }

        // the binder dialect comes from the connection provider and must be the dialect the statement was compiled for.
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

            // SQLA-05: a per-request timeout (declarative endpoint header) can only tighten the global WebSQL timeout.
            int commandTimeout = Math.Max(1, _options.Value.WebSql.ExecutionTimeoutSeconds);
            if (request.TimeoutSeconds is > 0)
            {
                commandTimeout = Math.Min(commandTimeout, request.TimeoutSeconds.Value);
            }

            if (dmlContext.IsDml)
            {
                // the session initializer gives no transaction on SQL Server, Oracle and SQLite, so one is begun here. From the
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
                    deliveredOrAffectedRows = affectedRows;
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
                await rowWriter(emptyReader, new GovernedResultInfo(Array.Empty<SqlResultColumn>(), ImmutableArray<bool>.Empty), ct).ConfigureAwait(false);
                return compiled.Sql;
            }

            await using var command = connection.CreateCommand();
            if (tx != null)
            {
                command.Transaction = tx;
            }

            command.CommandTimeout = commandTimeout;
            binder.Bind(command, compiled, clientValues);

            // the execution and the streaming of the rows run under the provider guard (SQLite interrupts at the timeout).
            bool isTruncated = false;
            await _executionGuard.RunAsync(
                connection,
                TimeSpan.FromSeconds(commandTimeout),
                async () =>
                {
                    await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false))
                    {
                        var columns = SqlResultColumns.Describe(reader);
                        var maskArray = compiled.OutputColumnMasks;
                        if (maskArray.IsDefault || (reader.FieldCount > 0 && maskArray.Length != reader.FieldCount))
                        {
                            throw new GovernedSqlException(GovernedSqlErrorCodes.CompilerInvariant, compiled.Dialect);
                        }
                        var info = new GovernedResultInfo(columns, reader.FieldCount == 0 ? ImmutableArray<bool>.Empty : maskArray);
                        if (rewrite.DeliveredRowLimit > 0)
                        {
                            var limitedReader = new RowLimitedDataReader(reader, rewrite.DeliveredRowLimit);
                            await rowWriter(limitedReader, info, ct).ConfigureAwait(false);
                            deliveredOrAffectedRows = limitedReader.DeliveredRows;
                            isTruncated = limitedReader.HasMoreRows;
                        }
                        else
                        {
                            await rowWriter(reader, info, ct).ConfigureAwait(false);
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
            NotifyPageOutcome(request, user, tenantId, rewrite, isTruncated);
            return compiled.Sql;
        }
        catch (Exception ex)
        {
            // A driver error is reduced to a stable code first; the original is logged server-side only (see SanitizeProviderError).
            var sanitized = SanitizeProviderError(ex, binderDialect, dsName, BindsValueFromTheCaller(compiled, clientValues));
            var reported = sanitized ?? ex;

            if (!dmlContext.IsDml)
            {
                var (redactedOriginalSql, originalSqlHash) = _digester.Anonymize(request.Sql);
                var userSid = user.GetUserSid() ?? new Sid("anonymous");
                // the stable code and the exception type, never ex.Message (a driver message can carry key values).
                var failureDetails = new AuditDetailsBuilder()
                    .WithField("originalSql", redactedOriginalSql)
                    .WithField("originalSqlHash", originalSqlHash)
                    .WithField("dataSource", dsName)
                    .WithField("reasonCode", (ex is OperationCanceledException && ct.IsCancellationRequested) ? "CLIENT_ABORTED" : ex.GetType().Name)
                    .WithField("errorCode", ErrorCodeOf(reported))
                    .WithField("errorType", ex.GetType().Name);
                if (compiled.HasRecursiveCte)
                {
                    failureDetails.WithField("recursiveCte", true);   // failure, timeout and limit audits carry the flag too
                }

                var detailsJson = failureDetails.Build();

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
        catch
        {
            executionFailed = true;
            throw;
        }
        finally
        {
            executionSw.Stop();
            await SettleCostAsync(admission, deliveredOrAffectedRows, executionSw.Elapsed.TotalSeconds, executionFailed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when the statement binds a client list or a caller attribute value (BC-129-16): a driver message can quote such a value, so
    /// the exception text is not logged for it.
    /// </summary>
    internal static bool BindsValueFromTheCaller(CompiledSql compiled, IReadOnlyDictionary<string, object?> clientValues) =>
        clientValues.Values.Any(v => v is ClientListValue) ||
        compiled.Parameters.Any(p => p.Origin == ParameterOrigin.Policy && p.SourceName != null && Autheris.Application.Identity.Attributes.CallerAttributeParameterName.IsReserved(p.SourceName));

    /// <summary>
    /// A driver error becomes a typed error with a stable code and a fixed message; the original is logged server-side only. A timeout
    /// or an unavailable source is classified first and keeps its own status (504, 503). Everything that is not a driver error
    /// (policy and binder refusals, cancellation, the gateway's own exceptions) passes through unchanged.
    /// </summary>
    private GovernedSqlException? SanitizeProviderError(Exception error, TargetSqlDialect dialect, string dataSourceName, bool clientListsBound)
    {
        if (error is GovernedSqlException || !IsProviderError(error))
        {
            return null;
        }

        if (DataAccessErrorClassifier.Classify(error) is DataAccessErrorKind.Timeout or DataAccessErrorKind.Unavailable)
        {
            return null;
        }

        var mapped = DmlErrorSanitizer.Map(dialect, error);

        // a recursion limit (SQL Server 530, Oracle ORA-32044) is logged by type and number only, never with the driver message.
        if (clientListsBound || mapped.Code == GovernedSqlErrorCodes.LimitExceeded)
        {
            // 07 / BC-129-16: a driver message can carry a bound list element or caller attribute value ("Conversion failed when converting the nvarchar value '...'"), so
            // the exception object (message, inner exceptions) is not logged when a client list is bound; its type and number are.
            var driver = error as DbException;
            _logger?.LogWarning("WebSQL provider error on data source {DataSource} ({Dialect}); the caller receives a sanitized error. ErrorType={ErrorType} ErrorCode={ErrorCode} TraceId={TraceId}",
                dataSourceName, dialect, error.GetType().Name, driver?.ErrorCode, Activity.Current?.TraceId.ToString());
        }
        else
        {
            _logger?.LogWarning(error, "WebSQL provider error on data source {DataSource} ({Dialect}); the caller receives a sanitized error. TraceId={TraceId}",
                dataSourceName, dialect, Activity.Current?.TraceId.ToString());
        }

        return mapped;
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

    private IReadOnlyDictionary<string, object?> BuildClientValues(GovernedSqlQueryRequest request, IReadOnlySet<string> referenced)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var parameters = request.Parameters;
        if (parameters != null)
        {
            AddClientValues(request, parameters, referenced, values);
        }

        // Issue #280: gateway-generated parameters of a planned read (typed scalars, validated by MergeGatewayParameters). Add, not the
        // indexer: a duplicate name throws instead of overwriting a client value.
        if (request.GatewayParameters is { } gatewayParameters)
        {
            foreach (var (name, value) in gatewayParameters)
            {
                values.Add(GovernedSqlExecutionService.GatewayParameterAlias(name), value);
            }
        }

        return values;
    }

    private void AddClientValues(
        GovernedSqlQueryRequest request,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlySet<string> referenced,
        Dictionary<string, object?> values)
    {
        var limits = ListParameterLimits.From(_options.Value.WebSql);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int lists = 0;
        foreach (var (name, value) in parameters)
        {
            // The first spelling wins when two names differ only by case (as before).
            string trimmed = name.TrimStart('@');
            if (Autheris.Application.Identity.Attributes.CallerAttributeParameterName.IsReserved(trimmed))
            {
                // 129 (BC-129-16, ST-129-14): a client parameter never uses the reserved name of a caller attribute slot (any case).
                throw new ArgumentException("A client parameter must not use a reserved gateway parameter name.");
            }

            if (!seen.Add(trimmed))
            {
                continue;
            }

            if (value is not ClientListValue && !ListParameterNormalizer.IsListCandidate(value))
            {
                values.Add(trimmed, WebSqlParameterValues.Normalize(value));
                continue;
            }

            // admission by allowlist, before any element is read.
            AdmitList(request, value);
            if (++lists > limits.MaxListParameters)
            {
                throw new ClientListException(GovernedSqlErrorCodes.ListParamTooLarge);
            }

            if (!referenced.Contains(trimmed))
            {
                continue;   // dropped: it neither enters the cache key nor costs a normalization
            }

            values.Add(trimmed, value switch
            {
                // An endpoint-built value is not trusted: the configured maximum applies again (its elements were validated by Create).
                ClientListValue list when list.Count > limits.ConfiguredMaxValues => throw new ClientListException(GovernedSqlErrorCodes.ListParamTooLarge),
                ClientListValue list => list,
                _ => ListParameterNormalizer.Normalize(value!, limits, null)
            });
        }
    }

    /// <summary>
    /// a list value is admitted only on WebSql and Trino (a JSON array) or SqlEndpoints (an already normalized
    /// <see cref="ClientListValue"/>), never on a planned table read, never with another transport or none, never as a loose collection.
    /// </summary>
    private static void AdmitList(GovernedSqlQueryRequest request, object? value)
    {
        string? transport = request.Transport;
        bool admitted = !request.PlannedTableRead && value switch
        {
            JsonElement => transport is SqlTransports.WebSql or SqlTransports.Trino,
            ClientListValue => transport is SqlTransports.SqlEndpoints,
            _ => false
        };
        if (!admitted)
        {
            throw new ClientListException(GovernedSqlErrorCodes.ListParamPosition);
        }
    }

    private void NotifyPageOutcome(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, GovernedRewrite? rewrite, bool isTruncated)
    {
        if (request.Page == null || request.OnPageOutcome == null)
        {
            return;
        }

        var pageResponse = new GovernedSqlPageResponse(request.Page.Number, request.Page.Offset, request.Page.Size);
        string? nextToken = null;
        string? pagingUnavailable = rewrite?.PagingUnavailable;
        if (pagingUnavailable == null && isTruncated)
        {
            long nextOffset = checked(request.Page.Offset + request.Page.Size);
            long maxOffset = _options.Value.WebSql.Paging?.MaxOffset ?? 10_000;
            if (nextOffset > maxOffset)
            {
                pagingUnavailable = "offset_limit";
            }
            else if (user.GetUserSid() == null)
            {
                pagingUnavailable = "caller_not_bindable";
            }
            else
            {
                string sourceId = rewrite?.DataSourceName ?? _options.Value.WebSql.DefaultDataSourceName;
                nextToken = _pageTokenCodec.EncodeToken(
                    request.Sql,
                    request.Parameters,
                    user,
                    tenantId,
                    sourceId,
                    request.Transport ?? "WebSql",
                    nextOffset,
                    request.Page.Size,
                    request.Page.Number + 1,
                    TimeSpan.FromMinutes(_options.Value.WebSql.Paging?.TokenLifetimeMinutes ?? 15));
            }
        }
        request.OnPageOutcome(new GovernedSqlPageOutcome(pageResponse, nextToken, pagingUnavailable));
    }

    private static void AddCallerAttributeAuditFields(Dictionary<string, object?> fields, GovernedRewrite rewrite, CompiledSql compiled)
    {
        if (rewrite.CallerAttributes is not { Count: > 0 } attributes)
        {
            return;
        }

        fields["caller_attributes"] = attributes.ToDictionary(
            p => p.Key,
            p => p.Value.Select(a => new Dictionary<string, object?> { ["id"] = a.Id, ["outcome"] = a.Outcome, ["reason"] = a.Reason }).ToList(),
            StringComparer.OrdinalIgnoreCase);
        fields["caller_attribute_binds"] = compiled.Parameters.Count(p => p.Origin == ParameterOrigin.Policy && p.SourceName != null && Autheris.Application.Identity.Attributes.CallerAttributeParameterName.IsReserved(p.SourceName));
    }
}
