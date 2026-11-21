namespace Autheris.Application.Sql.Services;

using Autheris.Application.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Application.DataSources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// Thread-safe in-memory statement manager implementing the Trino REST client query lifecycle
/// (wait_timeout fast-path, continuation polling via nextUri, and cancellation).
/// </summary>
public sealed class WebSqlStatementManager : IWebSqlStatementManager, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebSqlStatementManager>? _logger;
    private readonly TimeSpan _retentionPeriod;
    private readonly int _maxSessionsPerUser;
    private readonly int _maxTotalSessions;
    private readonly ConcurrentDictionary<string, StatementSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _capacityLock = new();
    private readonly Dictionary<(TenantId Tenant, string UserSid), int> _inFlightUserReservations = new();
    private int _inFlightGlobalReservations;
    private readonly Timer? _cleanupTimer;
    private long _statementCounter;
    private bool _disposed;

    private static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxWaitTimeout = TimeSpan.FromSeconds(300);

    private readonly GatewayOptions? _gatewayOptions;

    public WebSqlStatementManager(
        IServiceScopeFactory scopeFactory,
        ILogger<WebSqlStatementManager>? logger = null,
        IOptions<GatewayOptions>? options = null,
        TimeSpan? retentionPeriod = null,
        int? maxSessionsPerUser = null,
        int? maxTotalSessions = null,
        IServiceProviderIsService? servicePresence = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        if (servicePresence is not null)
        {
            if (!servicePresence.IsService(typeof(Autheris.Application.Catalog.Discovery.IDiscoveryService)))
            {
                throw new InvalidOperationException("IDiscoveryService must be registered to construct WebSqlStatementManager.");
            }

            if (!servicePresence.IsService(typeof(Autheris.Application.Policy.Pdp.IAccessDecisionPoint)))
            {
                throw new InvalidOperationException("IAccessDecisionPoint must be registered to construct WebSqlStatementManager.");
            }
        }

        _logger = logger;
        _gatewayOptions = options?.Value;

        var webSqlOpt = options?.Value?.WebSql;
        _retentionPeriod = retentionPeriod ?? (webSqlOpt != null && webSqlOpt.StatementRetentionMinutes > 0
            ? TimeSpan.FromMinutes(webSqlOpt.StatementRetentionMinutes)
            : DefaultRetention);
        _maxSessionsPerUser = maxSessionsPerUser ?? (webSqlOpt?.MaxConcurrentSessionsPerUser > 0 ? webSqlOpt.MaxConcurrentSessionsPerUser : 10);
        _maxTotalSessions = maxTotalSessions ?? (webSqlOpt?.MaxTotalStatementSessions > 0 ? webSqlOpt.MaxTotalStatementSessions : 1000);
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public WebSqlStatementManager(
        IServiceScopeFactory scopeFactory,
        ILogger<WebSqlStatementManager>? logger,
        TimeSpan? retentionPeriod)
        : this(scopeFactory, logger, options: null, retentionPeriod: retentionPeriod)
    {
    }

    public async Task<StatementExecutionStatus> SubmitOrWaitAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        Sid owner,
        TenantId tenantId,
        TimeSpan waitTimeout,
        DataSourceTransportFlags ingress = DataSourceTransportFlags.None,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(owner.Value))
        {
            throw new ArgumentException("Owner Sid must not be null or whitespace.", nameof(owner));
        }

        string userSid = owner.Value;
        var reservationKey = (tenantId, userSid);
        ReserveCapacity(reservationKey);
        bool committed = false;
        CancellationTokenSource? sessionCts = null;

        try
        {
            if (!request.PlannedTableRead && request.Admission == null)
            {
                using var admitScope = _scopeFactory.CreateScope();
                var costQuota = admitScope.ServiceProvider.GetService<Autheris.Application.Common.Quota.IQueryCostQuota>();
                var sqlEngine = admitScope.ServiceProvider.GetService<ISqlEngine>();
                if (costQuota != null && sqlEngine != null)
                {
                    var meta = await sqlEngine.AnalyzeAsync(request.Sql.AsMemory(), null, ct).ConfigureAwait(false);
                    var rowLimit = request.RowLimit ?? SqlRowLimit.For(_gatewayOptions?.WebSql ?? new WebSqlOptions());
                    var costOpts = _gatewayOptions?.QueryCost ?? new QueryCostOptions();
                    var analysisOpts = new Autheris.Application.Governance.Analysis.QueryAnalysisOptions();
                    var webSqlCost = _gatewayOptions?.WebSql?.Cost ?? new WebSqlCostOptions();

                    int cEst = Autheris.Application.Sql.Cost.SqlStatementCostEstimator.EstimateCost(
                        meta,
                        rowLimit,
                        costOpts,
                        analysisOpts,
                        webSqlCost,
                        pageOffset: request.PageOffset ?? 0,
                        isPaged: request.IsPaged,
                        maxAffectedRows: _gatewayOptions?.WebSql?.MaxAffectedRows ?? 1000);

                    var admission = await costQuota.AdmitAsync(
                        new Autheris.Application.Common.Quota.QueryCostRequest(
                            Principal: user,
                            ApiKey: null,
                            ClientIp: null,
                            EstimatedCost: cEst,
                            TransportMaxCost: webSqlCost.MaxStatementCost,
                            CapByTierMaxCostPerQuery: false),
                        ct).ConfigureAwait(false);

                    if (admission.Outcome == Autheris.Application.Common.Quota.QueryCostOutcome.OverCost)
                    {
                        throw new SqlCostExceededException(cEst, admission.MaxAllowedCost);
                    }
                    if (admission.Outcome == Autheris.Application.Common.Quota.QueryCostOutcome.QuotaExhausted)
                    {
                        throw new SqlQuotaExhaustedException(admission.RetryAfterSeconds);
                    }

                    request = request with { Admission = admission };
                }
            }

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            long counter = Interlocked.Increment(ref _statementCounter);
            string statementId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{counter:00000}_{RandomNumberGenerator.GetHexString(32, lowercase: true)}";
            sessionCts = new CancellationTokenSource();
            var sessionToken = sessionCts.Token;
            var sw = Stopwatch.StartNew();

            var sources = new SourceHolder();
            var executionTask = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                // Issue #447: the background scope carries the ingress of the submitting request. An unstamped submit
                // (None) leaves the scope unset, which the transport gate denies.
                if (ingress != DataSourceTransportFlags.None)
                {
                    scope.ServiceProvider.GetService<IIngressTransportContext>()?.Set(ingress);
                }

                GovernedSqlResult? metadataResult = null;
                var metadataRowLimit = (int)Math.Min(int.MaxValue, (request.RowLimit ?? SqlRowLimit.For(_gatewayOptions?.WebSql ?? new WebSqlOptions())).Effective(null));
                if (string.Equals(request.Transport, SqlTransports.Trino, StringComparison.Ordinal))
                {
                    var metadataRepo = scope.ServiceProvider.GetService<ITableMetadataRepository>();
                    var consentRepo = scope.ServiceProvider.GetService<IConsentRepository>();
                    var consentResolver = scope.ServiceProvider.GetService<IConsentResolutionService>();
                    var discovery = scope.ServiceProvider.GetRequiredService<Autheris.Application.Catalog.Discovery.IDiscoveryService>();
                    var decisionPoint = scope.ServiceProvider.GetRequiredService<Autheris.Application.Policy.Pdp.IAccessDecisionPoint>();
                    var visibility = new TrinoMetadataVisibility(discovery, decisionPoint);
                    metadataResult = await TrinoMetadataQueryHandler.TryExecuteAsync(
                        request.Sql, user, tenantId, metadataRepo, consentRepo, _gatewayOptions, consentResolver, visibility, metadataRowLimit, sessionToken).ConfigureAwait(false);
                }
                if (metadataResult != null)
                {
                    if (request.Admission != null)
                    {
                        try
                        {
                            var costQuota = scope.ServiceProvider.GetService<Autheris.Application.Common.Quota.IQueryCostQuota>();
                            if (costQuota != null)
                            {
                                await costQuota.SettleAsync(request.Admission, 1, CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                        catch { }
                    }
                    if (metadataResult.Rows.Count > metadataRowLimit)
                    {
                        metadataResult = metadataResult with
                        {
                            Rows = metadataResult.Rows.Take(metadataRowLimit).ToList(),
                            RowCount = metadataRowLimit,
                            Truncated = true
                        };
                    }

                    return metadataResult;
                }

                var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();
                try
                {
                    return await sqlService.ExecuteQueryBufferedAsync(request, user, tenantId, sessionToken).ConfigureAwait(false);
                }
                finally
                {
                    // Issue #447: the sources the choke points checked, kept to re-check later pages.
                    sources.Names = scope.ServiceProvider.GetService<IDataSourceTransportGate>()?.CheckedSources ?? [];
                    if (request.Admission != null)
                    {
                        try
                        {
                            var costQuota = scope.ServiceProvider.GetService<Autheris.Application.Common.Quota.IQueryCostQuota>();
                            if (costQuota != null)
                            {
                                await costQuota.SettleAsync(request.Admission, 1, CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                        catch { }
                    }
                }
            }, sessionToken);

            var session = new StatementSession(statementId, tenantId, userSid, DateTimeOffset.UtcNow, sw, sessionCts, executionTask, ingress, sources);
            CommitSession(reservationKey, session);
            committed = true;

            return await WaitForSessionAsync(session, waitTimeout, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!committed)
            {
                ReleaseReservation(reservationKey);
                if (sessionCts != null)
                {
                    try { sessionCts.Cancel(); } catch { }
                    try { sessionCts.Dispose(); } catch { }
                }
            }
        }
    }

    public async Task<StatementExecutionStatus> GetStatusOrWaitAsync(
        string statementId,
        ClaimsPrincipal user,
        Sid owner,
        TenantId tenantId,
        TimeSpan waitTimeout,
        DataSourceTransportFlags ingress = DataSourceTransportFlags.None,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statementId);
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(owner.Value))
        {
            throw new ArgumentException("Owner Sid must not be null or whitespace.", nameof(owner));
        }

        if (!_sessions.TryGetValue(statementId, out var session))
        {
            throw new KeyNotFoundException($"Statement '{statementId}' was not found or has expired.");
        }

        ValidateSessionOwnership(session, owner, tenantId);

        // Issue #447: the statement is only served over the ingress that created it, and every page is
        // re-checked against the live source configuration. A denial returns a FAILED status without data.
        if (await RecheckTransportAsync(session, owner, tenantId, ingress, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var status = await WaitForSessionAsync(session, waitTimeout, ct).ConfigureAwait(false);
        if (status.State == "FINISHED" &&
            await RecheckTransportAsync(session, owner, tenantId, ingress, ct).ConfigureAwait(false) is { } deniedAfterWait)
        {
            return deniedAfterWait;
        }

        return status;
    }

    private async Task<StatementExecutionStatus?> RecheckTransportAsync(
        StatementSession session, Sid owner, TenantId tenantId, DataSourceTransportFlags ingress, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;
        var gate = provider.GetService<IDataSourceTransportGate>() ?? DenyAllTransportGate.Instance;
        try
        {
            if (ingress != DataSourceTransportFlags.None)
            {
                provider.GetService<IIngressTransportContext>()?.Set(ingress);
            }

            // The audit context of this fresh scope carries the fetching caller, so the denial event names the right actor and tenant.
            if (provider.GetService<Autheris.Domain.Audit.AuditContext>() is { } auditContext)
            {
                auditContext.ActorSid = owner;
                auditContext.TenantId = tenantId;
            }

            if (session.Ingress != ingress)
            {
                var recorder = provider.GetService<ITransportDenialRecorder>();
                if (recorder != null)
                {
                    await recorder.RecordDenialAsync(
                        new TransportDenial("statement-binding", null, ingress, TransportDecisionReason.IngressMismatch), ct).ConfigureAwait(false);
                }

                throw new Autheris.Domain.Exceptions.TransportNotAllowedException(null, ingress);
            }

            var names = session.Sources.Names;
            if (names.Count > 0)
            {
                await gate.EnsureCatalogSourcesAsync(names, ct).ConfigureAwait(false);
            }

            return null;
        }
        catch (Autheris.Domain.Exceptions.TransportNotAllowedException)
        {
            return new StatementExecutionStatus(
                session.StatementId, "FAILED", null, null, session.Stopwatch.ElapsedMilliseconds,
                Failure: new SqlFailure(SqlFailureKind.TransportDenied, "Access to the data source is not allowed over this transport.", Autheris.Domain.Exceptions.TransportNotAllowedException.Code));
        }
    }

    public Task<bool> CancelStatementAsync(
        string statementId,
        ClaimsPrincipal user,
        Sid owner,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statementId);
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(owner.Value))
        {
            throw new ArgumentException("Owner Sid must not be null or whitespace.", nameof(owner));
        }

        if (!_sessions.TryGetValue(statementId, out var session))
        {
            return Task.FromResult(false);
        }

        ValidateSessionOwnership(session, owner, tenantId);
        session.IsCancelled = true;
        try { session.Cts.Cancel(); } catch (ObjectDisposedException) { }
        return Task.FromResult(true);
    }

    private async Task<StatementExecutionStatus> WaitForSessionAsync(
        StatementSession session,
        TimeSpan waitTimeout,
        CancellationToken ct)
    {
        if (session.ExecutionTask.IsCompleted || waitTimeout <= TimeSpan.Zero)
        {
            return BuildStatus(session);
        }

        var effectiveTimeout = waitTimeout > MaxWaitTimeout ? MaxWaitTimeout : waitTimeout;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(effectiveTimeout, linkedCts.Token);

        var completedTask = await Task.WhenAny(session.ExecutionTask, delayTask).ConfigureAwait(false);
        if (completedTask == session.ExecutionTask)
        {
            try { linkedCts.Cancel(); } catch (ObjectDisposedException) { }
        }

        return BuildStatus(session);
    }

    private StatementExecutionStatus BuildStatus(StatementSession session)
    {
        long elapsedMillis = session.Stopwatch.ElapsedMilliseconds;

        if (session.IsCancelled)
        {
            return new StatementExecutionStatus(
                session.StatementId, "CANCELED", null, null, elapsedMillis,
                Failure: new SqlFailure(SqlFailureKind.Canceled, SqlFailureClassifier.ClientCanceledMessage));
        }

        if (session.ExecutionTask.IsCanceled && CanceledGovernedError(session.ExecutionTask) is { } governedFailure)
        {
            return new StatementExecutionStatus(
                session.StatementId, "FAILED", null, null, elapsedMillis,
                Failure: SqlFailureClassifier.Classify(governedFailure));
        }

        if (session.ExecutionTask.IsCanceled)
        {
            return new StatementExecutionStatus(
                session.StatementId, "CANCELED", null, null, elapsedMillis,
                Failure: new SqlFailure(SqlFailureKind.Canceled, SqlFailureClassifier.GenericCanceledMessage));
        }

        if (session.ExecutionTask.IsFaulted)
        {
            var ex = session.ExecutionTask.Exception?.InnerException ?? session.ExecutionTask.Exception;
            _logger?.Error(ex, "WebSQL async execution faulted for statement {StatementId}", session.StatementId);
            return new StatementExecutionStatus(
                session.StatementId, "FAILED", null, null, elapsedMillis,
                Failure: SqlFailureClassifier.Classify(ex));
        }

        if (session.ExecutionTask.IsCompletedSuccessfully)
        {
            var result = session.ExecutionTask.Result;
            var columns = result.Columns;
            var data = ConvertRowsTo2DArray(result.Columns, result.Rows);
            var columnTypes = EncodeTrinoTypes(result.ColumnDescriptions, columns, data);
            return new StatementExecutionStatus(
                session.StatementId, "FINISHED", columns, data, elapsedMillis,
                Failure: null,
                Truncated: result.Truncated,
                ColumnTypes: columnTypes);
        }

        return new StatementExecutionStatus(session.StatementId, "RUNNING", null, null, elapsedMillis);
    }

    private static IReadOnlyList<TrinoColumnType> EncodeTrinoTypes(
        IReadOnlyList<SqlResultColumn>? descriptions,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> data)
    {
        var types = new TrinoColumnType[columns.Count];
        if (descriptions == null || descriptions.Count != columns.Count)
        {
            Array.Fill(types, TrinoColumnType.Varchar);
            return types;
        }

        for (int c = 0; c < columns.Count; c++)
        {
            int column = c;
            types[c] = TrinoColumnTypes.Map(descriptions[c], data.Select(row => row[column]));
        }

        foreach (var row in data)
        {
            var values = (object?[])row;
            for (int c = 0; c < types.Length; c++)
            {
                values[c] = TrinoColumnTypes.Encode(values[c], types[c]);
            }
        }

        return types;
    }

    private static IReadOnlyList<IReadOnlyList<object?>> ConvertRowsTo2DArray(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.Count == 0 || columns.Count == 0)
        {
            return Array.Empty<IReadOnlyList<object?>>();
        }

        var result = new List<IReadOnlyList<object?>>(rows.Count);
        for (int r = 0; r < rows.Count; r++)
        {
            var rowDict = rows[r];
            var rowValues = new object?[columns.Count];
            for (int c = 0; c < columns.Count; c++)
            {
                rowDict.TryGetValue(columns[c], out var val);
                rowValues[c] = val;
            }
            result.Add(rowValues);
        }

        return result;
    }

    private static void ValidateSessionOwnership(StatementSession session, Sid owner, TenantId tenantId)
    {
        if (!session.TenantId.Equals(tenantId) || !string.Equals(session.UserSid, owner.Value, StringComparison.Ordinal))
        {
            throw new SecurityException("Statement does not belong to the calling user or tenant.");
        }
    }

    private void ReserveCapacity((TenantId Tenant, string UserSid) key)
    {
        lock (_capacityLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PurgeExpiredSessionsCore();

            int userReserved = _inFlightUserReservations.GetValueOrDefault(key);
            int activeUserCount = userReserved;
            List<StatementSession>? userCompletedSessions = null;
            int totalUserSessions = userReserved;

            foreach (var s in _sessions.Values)
            {
                if (s.UserSid == key.UserSid && s.TenantId == key.Tenant)
                {
                    totalUserSessions++;
                    if (!s.ExecutionTask.IsCompleted) activeUserCount++;
                    else (userCompletedSessions ??= new()).Add(s);
                }
            }

            if (activeUserCount >= _maxSessionsPerUser)
            {
                throw new Autheris.Domain.Exceptions.GatewayThrottledException(
                    $"User '{key.UserSid}' has reached the limit of {_maxSessionsPerUser} concurrent active statements. Wait for existing statements to complete.",
                    retryAfterSeconds: 5);
            }

            if (totalUserSessions >= _maxSessionsPerUser && userCompletedSessions != null)
            {
                foreach (var old in userCompletedSessions.OrderBy(s => s.CreatedAt).Take(totalUserSessions - _maxSessionsPerUser + 1))
                {
                    if (_sessions.TryRemove(old.StatementId, out var removed))
                    {
                        try { removed.Cts.Dispose(); } catch { }
                    }
                }
            }

            if (_sessions.Count + _inFlightGlobalReservations >= _maxTotalSessions)
            {
                var globalCompleted = _sessions.Values
                    .Where(s => s.ExecutionTask.IsCompleted)
                    .OrderBy(s => s.CreatedAt)
                    .Take(Math.Max(1, _sessions.Count + _inFlightGlobalReservations - _maxTotalSessions + 1))
                    .ToList();

                foreach (var old in globalCompleted)
                {
                    if (_sessions.TryRemove(old.StatementId, out var removed))
                    {
                        try { removed.Cts.Dispose(); } catch { }
                    }
                }

                if (_sessions.Count + _inFlightGlobalReservations >= _maxTotalSessions)
                {
                    throw new Autheris.Domain.Exceptions.GatewayThrottledException(
                        "Global statement session capacity exceeded. Please retry later.",
                        retryAfterSeconds: 5);
                }
            }

            _inFlightUserReservations[key] = userReserved + 1;
            _inFlightGlobalReservations++;
        }
    }

    private void ReleaseReservation((TenantId Tenant, string UserSid) key)
    {
        lock (_capacityLock) { ReleaseReservationCore(key); }
    }

    private void ReleaseReservationCore((TenantId Tenant, string UserSid) key)
    {
        if (_inFlightUserReservations.TryGetValue(key, out int count))
        {
            if (count <= 1) _inFlightUserReservations.Remove(key);
            else _inFlightUserReservations[key] = count - 1;
        }
        if (_inFlightGlobalReservations > 0) _inFlightGlobalReservations--;
    }

    private void CommitSession((TenantId Tenant, string UserSid) key, StatementSession session)
    {
        lock (_capacityLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReleaseReservationCore(key);
            _sessions[session.StatementId] = session;
        }
    }

    private void CleanupExpiredSessions(object? state) => PurgeExpiredSessions();

    private void PurgeExpiredSessions()
    {
        lock (_capacityLock) { PurgeExpiredSessionsCore(); }
    }

    private void PurgeExpiredSessionsCore()
    {
        var cutoff = DateTimeOffset.UtcNow - _retentionPeriod;
        foreach (var (id, session) in _sessions)
        {
            if (session.CreatedAt < cutoff && session.ExecutionTask.IsCompleted && _sessions.TryRemove(id, out var removed))
            {
                try { removed.Cts.Dispose(); } catch (ObjectDisposedException) { }
            }
        }
    }

    public void Dispose()
    {
        lock (_capacityLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _cleanupTimer?.Dispose();
        foreach (var (_, session) in _sessions)
        {
            try { session.Cts.Cancel(); } catch { }
            try { session.Cts.Dispose(); } catch { }
        }
        _sessions.Clear();
    }

    private static Exception? CanceledGovernedError(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex) when (Errors.SqlCompilerErrorMapper.TryMap(ex, out _))
        {
            return ex;
        }
        catch
        {
            return null;
        }
    }



    private sealed class SourceHolder
    {
        public volatile IReadOnlyList<string> Names = [];
    }

    private sealed class StatementSession(
        string statementId,
        TenantId tenantId,
        string userSid,
        DateTimeOffset createdAt,
        Stopwatch stopwatch,
        CancellationTokenSource cts,
        Task<GovernedSqlResult> executionTask,
        DataSourceTransportFlags ingress,
        SourceHolder sources)
    {
        public SourceHolder Sources { get; } = sources;
        public string StatementId { get; } = statementId;
        public TenantId TenantId { get; } = tenantId;
        public string UserSid { get; } = userSid;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public Stopwatch Stopwatch { get; } = stopwatch;
        public CancellationTokenSource Cts { get; } = cts;
        public Task<GovernedSqlResult> ExecutionTask { get; } = executionTask;

        /// <summary>The ingress transport that submitted the statement (05 binding input).</summary>
        public DataSourceTransportFlags Ingress { get; } = ingress;
        public volatile bool IsCancelled;
    }
}
