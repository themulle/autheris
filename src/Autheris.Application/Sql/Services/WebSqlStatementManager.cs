namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Thread-safe in-memory statement manager implementing the Trino REST client query lifecycle
/// (wait_timeout fast-path, continuation polling via nextUri, and cancellation).
/// </summary>
public sealed class WebSqlStatementManager : IWebSqlStatementManager, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebSqlStatementManager>? _logger;
    private readonly TimeSpan _retentionPeriod;
    private readonly ConcurrentDictionary<string, StatementSession> _sessions = new(StringComparer.Ordinal);
    private readonly Timer? _cleanupTimer;
    private long _statementCounter;
    private bool _disposed;

    private static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxWaitTimeout = TimeSpan.FromSeconds(300);

    public WebSqlStatementManager(
        IServiceScopeFactory scopeFactory,
        ILogger<WebSqlStatementManager>? logger = null,
        TimeSpan? retentionPeriod = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger;
        _retentionPeriod = retentionPeriod ?? DefaultRetention;

        // Clean up expired sessions periodically every 2 minutes
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
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
        long counter = Interlocked.Increment(ref _statementCounter);
        string statementId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{counter:00000}_{Guid.NewGuid().ToString("N")[..6]}";

        var sessionCts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        // Run execution in a separate scope to avoid captive dependency on Scoped IGovernedSqlExecutionService
        var executionTask = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();
            return await sqlService.ExecuteQueryBufferedAsync(request, user, tenantId, sessionCts.Token).ConfigureAwait(false);
        }, sessionCts.Token);

        var session = new StatementSession(
            statementId,
            tenantId,
            userSid,
            DateTimeOffset.UtcNow,
            sw,
            sessionCts,
            executionTask);

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
        try
        {
            session.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

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
                session.StatementId,
                "CANCELED",
                Columns: null,
                Data: null,
                NextUri: null,
                ErrorMessage: "Query execution was canceled by the client.",
                ElapsedTimeMillis: elapsedMillis);
        }

        if (session.ExecutionTask.IsCanceled)
        {
            return new StatementExecutionStatus(
                session.StatementId,
                "CANCELED",
                Columns: null,
                Data: null,
                NextUri: null,
                ErrorMessage: "Query execution timed out or was canceled.",
                ElapsedTimeMillis: elapsedMillis);
        }

        if (session.ExecutionTask.IsFaulted)
        {
            var ex = session.ExecutionTask.Exception?.InnerException ?? session.ExecutionTask.Exception;
            _logger?.LogError(ex, "WebSQL async execution faulted for statement {StatementId}", session.StatementId);

            return new StatementExecutionStatus(
                session.StatementId,
                "FAILED",
                Columns: null,
                Data: null,
                NextUri: null,
                ErrorMessage: SanitizeErrorMessage(ex),
                ElapsedTimeMillis: elapsedMillis);
        }

        if (session.ExecutionTask.IsCompletedSuccessfully)
        {
            var result = session.ExecutionTask.Result;
            var columns = result.Columns;
            var data = ConvertRowsTo2DArray(result.Columns, result.Rows);
            var columnTypes = EncodeTrinoTypes(result.ColumnDescriptions, columns, data);

            return new StatementExecutionStatus(
                session.StatementId,
                "FINISHED",
                Columns: columns,
                Data: data,
                NextUri: null,
                ErrorMessage: null,
                ElapsedTimeMillis: elapsedMillis,
                ColumnTypes: columnTypes);
        }

        // Still running: provide continuation URI
        return new StatementExecutionStatus(
            session.StatementId,
            "RUNNING",
            Columns: null,
            Data: null,
            NextUri: $"/v1/statement/queued/{session.StatementId}",
            ErrorMessage: null,
            ElapsedTimeMillis: elapsedMillis);
    }

    /// <summary>
    /// WebSQL findings 2.2: Trino type of every column, with the values in <paramref name="data"/> re-encoded in place
    /// for that type. Without column descriptions (or when they do not match the columns) every column is announced as
    /// varchar and the values stay unchanged.
    /// </summary>
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

        if (!session.TenantId.Equals(tenantId) ||
            !string.Equals(session.UserSid, currentSid, StringComparison.Ordinal))
        {
            throw new SecurityException("Statement does not belong to the calling user or tenant.");
        }
    }

    private static string ResolveUserSid(ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.PrimarySid)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? "anonymous";
    }

    private void CleanupExpiredSessions(object? state)
    {
        PurgeExpiredSessions();
    }

    private void PurgeExpiredSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - _retentionPeriod;
        foreach (var (id, session) in _sessions)
        {
            if (session.CreatedAt < cutoff && session.ExecutionTask.IsCompleted)
            {
                if (_sessions.TryRemove(id, out var removed))
                {
                    try { removed.Cts.Dispose(); } catch (ObjectDisposedException) { }
                }
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

    private static string SanitizeErrorMessage(Exception? ex)
    {
        if (ex == null)
        {
            return "Statement execution failed.";
        }

        if (ex is OperationCanceledException or TimeoutException)
        {
            return "Query execution timed out or was canceled.";
        }

        if (ex is Autheris.Domain.Exceptions.GatewayInvalidQueryException or
                  Antlr4.Runtime.Misc.ParseCanceledException or
                  ArgumentException)
        {
            return ex.Message;
        }

        if (ex is Autheris.Domain.Exceptions.GatewaySecurityException or
                  System.Security.SecurityException)
        {
            return "Access denied.";
        }

        return "The SQL statement could not be executed. Contact support with the trace id.";
    }

    private sealed class StatementSession
    {
        public string StatementId { get; }
        public TenantId TenantId { get; }
        public string UserSid { get; }
        public DateTimeOffset CreatedAt { get; }
        public Stopwatch Stopwatch { get; }
        public CancellationTokenSource Cts { get; }
        public Task<GovernedSqlResult> ExecutionTask { get; }
        public volatile bool IsCancelled;

        public StatementSession(
            string statementId,
            TenantId tenantId,
            string userSid,
            DateTimeOffset createdAt,
            Stopwatch stopwatch,
            CancellationTokenSource cts,
            Task<GovernedSqlResult> executionTask)
        {
            StatementId = statementId;
            TenantId = tenantId;
            UserSid = userSid;
            CreatedAt = createdAt;
            Stopwatch = stopwatch;
            Cts = cts;
            ExecutionTask = executionTask;
        }
    }
}
