namespace Autheris.Application.Sql.Services;

using Autheris.Application.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        int? maxTotalSessions = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
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
        TenantId tenantId,
        TimeSpan waitTimeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);

        PurgeExpiredSessions();
        string userSid = ResolveUserSid(user);

        int activeUserCount = 0;
        List<StatementSession>? userCompletedSessions = null;
        int totalUserSessions = 0;

        foreach (var s in _sessions.Values)
        {
            if (s.UserSid == userSid && s.TenantId == tenantId)
            {
                totalUserSessions++;
                if (!s.ExecutionTask.IsCompleted)
                {
                    activeUserCount++;
                }
                else
                {
                    userCompletedSessions ??= new();
                    userCompletedSessions.Add(s);
                }
            }
        }

        if (activeUserCount >= _maxSessionsPerUser)
        {
            throw new Autheris.Domain.Exceptions.GatewayThrottledException(
                $"User '{userSid}' has reached the limit of {_maxSessionsPerUser} concurrent active statements. Wait for existing statements to complete.",
                retryAfterSeconds: 5);
        }

        if (totalUserSessions >= _maxSessionsPerUser && userCompletedSessions != null)
        {
            var toEvict = userCompletedSessions.OrderBy(s => s.CreatedAt).Take(totalUserSessions - _maxSessionsPerUser + 1);
            foreach (var old in toEvict)
            {
                if (_sessions.TryRemove(old.StatementId, out var removed))
                {
                    try { removed.Cts.Dispose(); } catch { }
                }
            }
        }

        if (_sessions.Count >= _maxTotalSessions)
        {
            var globalCompleted = _sessions.Values
                .Where(s => s.ExecutionTask.IsCompleted)
                .OrderBy(s => s.CreatedAt)
                .Take(Math.Max(1, _sessions.Count - _maxTotalSessions + 1))
                .ToList();

            foreach (var old in globalCompleted)
            {
                if (_sessions.TryRemove(old.StatementId, out var removed))
                {
                    try { removed.Cts.Dispose(); } catch { }
                }
            }

            if (_sessions.Count >= _maxTotalSessions)
            {
                throw new Autheris.Domain.Exceptions.GatewayThrottledException(
                    "Global statement session capacity exceeded. Please retry later.",
                    retryAfterSeconds: 5);
            }
        }

        long counter = Interlocked.Increment(ref _statementCounter);
        string statementId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{counter:00000}_{Guid.NewGuid().ToString("N")[..6]}";
        var sessionCts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        var executionTask = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var metadataRepo = scope.ServiceProvider.GetService<ITableMetadataRepository>();
            var consentRepo = scope.ServiceProvider.GetService<IConsentRepository>();
            var consentResolver = scope.ServiceProvider.GetService<IConsentResolutionService>();
            var metadataResult = await TrinoMetadataQueryHandler.TryExecuteAsync(
                request.Sql, user, tenantId, metadataRepo, consentRepo, _gatewayOptions, consentResolver, sessionCts.Token).ConfigureAwait(false);
            if (metadataResult != null)
            {
                return metadataResult;
            }

            var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();
            return await sqlService.ExecuteQueryBufferedAsync(request, user, tenantId, sessionCts.Token).ConfigureAwait(false);
        }, sessionCts.Token);

        var session = new StatementSession(statementId, tenantId, userSid, DateTimeOffset.UtcNow, sw, sessionCts, executionTask);
        _sessions[statementId] = session;

        return await WaitForSessionAsync(session, waitTimeout, ct).ConfigureAwait(false);
    }

    public async Task<StatementExecutionStatus> GetStatusOrWaitAsync(
        string statementId,
        ClaimsPrincipal user,
        TenantId tenantId,
        TimeSpan waitTimeout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statementId);
        ArgumentNullException.ThrowIfNull(user);

        if (!_sessions.TryGetValue(statementId, out var session))
        {
            throw new KeyNotFoundException($"Statement '{statementId}' was not found or has expired.");
        }

        ValidateSessionOwnership(session, user, tenantId);
        return await WaitForSessionAsync(session, waitTimeout, ct).ConfigureAwait(false);
    }

    public Task<bool> CancelStatementAsync(
        string statementId,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statementId);
        ArgumentNullException.ThrowIfNull(user);

        if (!_sessions.TryGetValue(statementId, out var session))
        {
            return Task.FromResult(false);
        }

        ValidateSessionOwnership(session, user, tenantId);
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
            return new StatementExecutionStatus(session.StatementId, "CANCELED", null, null, null, "Query execution was canceled by the client.", elapsedMillis);
        }

        if (session.ExecutionTask.IsCanceled && CanceledGovernedError(session.ExecutionTask) is { } governedFailure)
        {
            return new StatementExecutionStatus(session.StatementId, "FAILED", null, null, null, SanitizeErrorMessage(governedFailure), elapsedMillis);
        }

        if (session.ExecutionTask.IsCanceled)
        {
            return new StatementExecutionStatus(session.StatementId, "CANCELED", null, null, null, "Query execution timed out or was canceled.", elapsedMillis);
        }

        if (session.ExecutionTask.IsFaulted)
        {
            var ex = session.ExecutionTask.Exception?.InnerException ?? session.ExecutionTask.Exception;
            _logger?.Error(ex, "WebSQL async execution faulted for statement {StatementId}", session.StatementId);
            return new StatementExecutionStatus(session.StatementId, "FAILED", null, null, null, SanitizeErrorMessage(ex), elapsedMillis);
        }

        if (session.ExecutionTask.IsCompletedSuccessfully)
        {
            var result = session.ExecutionTask.Result;
            var columns = result.Columns;
            var data = ConvertRowsTo2DArray(result.Columns, result.Rows);
            var columnTypes = EncodeTrinoTypes(result.ColumnDescriptions, columns, data);
            return new StatementExecutionStatus(session.StatementId, "FINISHED", columns, data, null, null, elapsedMillis, columnTypes);
        }

        return new StatementExecutionStatus(session.StatementId, "RUNNING", null, null, $"/v1/statement/queued/{session.StatementId}", null, elapsedMillis);
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

    private static void ValidateSessionOwnership(StatementSession session, ClaimsPrincipal user, TenantId tenantId)
    {
        string currentSid = ResolveUserSid(user);
        if (!session.TenantId.Equals(tenantId) || !string.Equals(session.UserSid, currentSid, StringComparison.Ordinal))
        {
            throw new SecurityException("Statement does not belong to the calling user or tenant.");
        }
    }

    private static string ResolveUserSid(ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.PrimarySid)?.Value ?? user.FindFirst("sub")?.Value ?? "anonymous";

    private void CleanupExpiredSessions(object? state) => PurgeExpiredSessions();

    private void PurgeExpiredSessions()
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
        if (_disposed) return;
        _disposed = true;
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

    private static string SanitizeErrorMessage(Exception? ex)
    {
        if (ex == null) return "Statement execution failed.";
        if (Errors.SqlCompilerErrorMapper.TryMap(ex, out var mapped)) return mapped.Title;
        if (ex is OperationCanceledException or TimeoutException) return "Query execution timed out or was canceled.";
        if (ex is Autheris.Domain.Exceptions.GatewayInvalidQueryException) return "The SQL request is invalid.";
        if (ex is Autheris.Domain.Exceptions.GatewaySecurityException or SecurityException) return "Access denied.";
        return "The SQL statement could not be executed. Contact support with the trace id.";
    }

    private sealed class StatementSession(
        string statementId,
        TenantId tenantId,
        string userSid,
        DateTimeOffset createdAt,
        Stopwatch stopwatch,
        CancellationTokenSource cts,
        Task<GovernedSqlResult> executionTask)
    {
        public string StatementId { get; } = statementId;
        public TenantId TenantId { get; } = tenantId;
        public string UserSid { get; } = userSid;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public Stopwatch Stopwatch { get; } = stopwatch;
        public CancellationTokenSource Cts { get; } = cts;
        public Task<GovernedSqlResult> ExecutionTask { get; } = executionTask;
        public volatile bool IsCancelled;
    }
}
