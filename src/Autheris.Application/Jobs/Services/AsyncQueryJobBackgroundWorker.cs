namespace Autheris.Application.Jobs.Services;

using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Jobs.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed class AsyncQueryJobBackgroundWorker : BackgroundService
{
    private readonly IAsyncQueryJobManager _jobManager;
    private readonly IAsyncJobQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ILogger<AsyncQueryJobBackgroundWorker> _logger;
    private readonly IOptions<GatewayOptions>? _options;

    public AsyncQueryJobBackgroundWorker(
        IAsyncQueryJobManager jobManager,
        IAsyncJobQueue queue,
        IServiceScopeFactory scopeFactory,
        IAuditLogRepository auditLogRepository,
        ILogger<AsyncQueryJobBackgroundWorker>? logger = null,
        IOptions<GatewayOptions>? options = null)
    {
        _options = options;
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(auditLogRepository);
        _jobManager = jobManager;
        _queue = queue;
        _scopeFactory = scopeFactory;
        _auditLogRepository = auditLogRepository;
        _logger = logger ?? NullLogger<AsyncQueryJobBackgroundWorker>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AsyncQueryJobBackgroundWorker started.");

        var purgeTask = RunPurgeLoopAsync(stoppingToken);

        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessJobItemAsync(item, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("AsyncQueryJobBackgroundWorker stopping gracefully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in AsyncQueryJobBackgroundWorker loop.");
        }

        await purgeTask.ConfigureAwait(false);
    }

    /// <summary>SR-M-21: periodically removes finished jobs and their result files after the retention period.</summary>
    private async Task RunPurgeLoopAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, (_options?.Value?.AsyncJobs ?? new AsyncJobOptions()).PurgeIntervalMinutes));
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await PurgeOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task PurgeOnceAsync(CancellationToken ct)
    {
        var retention = TimeSpan.FromHours(Math.Max(0, (_options?.Value?.AsyncJobs ?? new AsyncJobOptions()).RetentionHours));
        try
        {
            await _jobManager.PurgeExpiredJobsAsync(retention, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Purging expired async jobs failed.");
        }
    }

    private async Task ProcessJobItemAsync(
        AsyncJobExecutionItem item,
        CancellationToken stoppingToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var runningJob = item.Job with
        {
            State = AsyncJobState.Running,
            StartedAt = startedAt
        };
        if (!await _queue.TryTransitionAsync(runningJob, stoppingToken).ConfigureAwait(false))
        {
            // Cancelled (or otherwise finished) while still queued: nothing to execute.
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, item.Cts.Token);
        string? writtenPath = null;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sqlExec = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

            // SR-H-05 defence in depth: re-derive the bounds server-side; the queued request is never trusted.
            var options = _options?.Value;
            linkedCts.CancelAfter(AsyncJobLimits.ResolveTimeout(item.Request, options));
            var queryRequest = new GovernedSqlQueryRequest(
                Sql: item.Request.Query,
                RowLimit: AsyncJobLimits.ResolveRowLimit(item.Request, options),
                Transport: SqlTransports.AsyncJob);

            var tenantId = new TenantId(item.Job.TenantId);
            var result = await sqlExec.ExecuteQueryBufferedAsync(
                queryRequest,
                item.User,
                tenantId,
                linkedCts.Token).ConfigureAwait(false);

            // Persist output to scratch directory (Pre-Storage DDM & RLS applied by GovernedSqlExecutionService)
            var tenantDir = Path.Combine(_queue.ScratchDirectory, item.Job.TenantId);
            Directory.CreateDirectory(tenantDir);

            var ext = item.Job.Format.Equals("csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "json";
            var filePath = Path.Combine(tenantDir, $"{item.Job.JobId}.{ext}");
            writtenPath = filePath;

            if (ext == "csv")
            {
                await WriteCsvResultAsync(filePath, result, linkedCts.Token).ConfigureAwait(false);
            }
            else
            {
                await WriteJsonResultAsync(filePath, result, linkedCts.Token).ConfigureAwait(false);
            }

            var fileInfo = new FileInfo(filePath);
            var completed = runningJob with
            {
                State = AsyncJobState.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                ResultFilePath = filePath,
                RowsProduced = result.RowCount,
                BytesProduced = fileInfo.Length
            };
            if (!await _queue.TryTransitionAsync(completed, stoppingToken).ConfigureAwait(false))
            {
                // Lost the race against a cancel: the job stays terminal and its result must not outlive it.
                try { File.Delete(filePath); } catch (Exception ex) { _logger.LogWarning(ex, "Could not delete result of cancelled job {JobId}", item.Job.JobId); }
                return;
            }

            await RecordAuditAsync(completed, item.User, AuditEventTypes.AsyncJobCompleted, "ALLOW", "Job completed successfully.", stoppingToken).ConfigureAwait(false);
        }
        // ParseCanceledException derives from OperationCanceledException: a governed SQL error (syntax, parser timeout) is a failed job.
        catch (OperationCanceledException ex) when (!Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.TryMap(ex, out _))
        {
            DeletePartialResult(writtenPath);
            var cancelled = runningJob with
            {
                State = AsyncJobState.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Job execution cancelled or timed out."
            };
            await _queue.TryTransitionAsync(cancelled, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DeletePartialResult(writtenPath);
            // WP-X0a (SEC-X1-01): the persisted message is the fixed message of the stable code, never Exception.Message (driver text, values).
            var sanitized = Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.FixedMessageOrDefault(ex, "The job could not be executed. Contact support with the trace id.");
            var failed = runningJob with
            {
                State = AsyncJobState.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = sanitized
            };
            await _queue.TryTransitionAsync(failed, stoppingToken).ConfigureAwait(false);
            _logger.LogError(ex, "Failed to execute async query job {JobId}", item.Job.JobId);
        }
    }

    private void DeletePartialResult(string? path)
    {
        if (path is null)
        {
            return;
        }

        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not delete partial job result."); }
    }

    private static async Task WriteJsonResultAsync(
        string filePath,
        GovernedSqlResult result,
        CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, result.Rows, cancellationToken: ct).ConfigureAwait(false);
    }

    private static async Task WriteCsvResultAsync(
        string filePath,
        GovernedSqlResult result,
        CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream);

        // Header
        await writer.WriteLineAsync(string.Join(",", result.Columns)).ConfigureAwait(false);

        // Rows
        foreach (var row in result.Rows)
        {
            ct.ThrowIfCancellationRequested();
            var values = new string[result.Columns.Count];
            for (int i = 0; i < result.Columns.Count; i++)
            {
                var col = result.Columns[i];
                var val = row.TryGetValue(col, out var obj) ? obj?.ToString() ?? string.Empty : string.Empty;
                values[i] = EscapeCsv(val);
            }
            await writer.WriteLineAsync(string.Join(",", values)).ConfigureAwait(false);
        }
    }

    internal static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // OWASP CSV Formula Injection defense (SEC-MED-16):
        // If the value starts with '=', '+', '-', '@', '\t', or '\r', prepend a single quote "'"
        if (value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') ||
            value.StartsWith('@') || value.StartsWith('\t') || value.StartsWith('\r'))
        {
            value = "'" + value;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private async Task RecordAuditAsync(
        AsyncJobDescriptor job,
        ClaimsPrincipal user,
        string eventType,
        string decision,
        string message,
        CancellationToken ct)
    {
        try
        {
            var sidStr = AsyncJobOwner.Resolve(user) ?? job.SubmittedByUserId;

            var entry = new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                TenantId = new TenantId(job.TenantId),
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = eventType,
                ActorSid = new Sid(sidStr),
                TargetTable = $"job:{job.JobId}",
                Decision = decision,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    JobId = job.JobId,
                    State = job.State.ToString(),
                    RowsProduced = job.RowsProduced,
                    BytesProduced = job.BytesProduced,
                    Message = message
                })
            };

            await _auditLogRepository.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record audit event for job {JobId}", job.JobId);
        }
    }
}
