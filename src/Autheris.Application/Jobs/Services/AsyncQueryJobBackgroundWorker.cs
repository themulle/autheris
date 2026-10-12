namespace Autheris.Application.Jobs.Services;

using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Jobs.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class AsyncQueryJobBackgroundWorker : BackgroundService
{
    private readonly IAsyncQueryJobManager _jobManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ILogger<AsyncQueryJobBackgroundWorker> _logger;

    public AsyncQueryJobBackgroundWorker(
        IAsyncQueryJobManager jobManager,
        IServiceScopeFactory scopeFactory,
        IAuditLogRepository auditLogRepository,
        ILogger<AsyncQueryJobBackgroundWorker>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(auditLogRepository);
        _jobManager = jobManager;
        _scopeFactory = scopeFactory;
        _auditLogRepository = auditLogRepository;
        _logger = logger ?? NullLogger<AsyncQueryJobBackgroundWorker>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_jobManager is not AsyncQueryJobManager concreteManager)
        {
            _logger.LogWarning("AsyncQueryJobBackgroundWorker requires concrete AsyncQueryJobManager. Worker exiting.");
            return;
        }

        _logger.LogInformation("AsyncQueryJobBackgroundWorker started.");

        try
        {
            await foreach (var item in concreteManager.ChannelReader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessJobItemAsync(concreteManager, item, stoppingToken).ConfigureAwait(false);
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
    }

    private async Task ProcessJobItemAsync(
        AsyncQueryJobManager manager,
        AsyncJobExecutionItem item,
        CancellationToken stoppingToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var runningJob = item.Job with
        {
            State = AsyncJobState.Running,
            StartedAt = startedAt
        };
        manager.UpdateJob(runningJob);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, item.Cts.Token);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sqlExec = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

            var maxRows = Math.Max(1, item.Request.MaxRows);
            var queryRequest = new GovernedSqlQueryRequest(
                Sql: item.Request.Query,
                RowLimit: new SqlRowLimit(maxRows, maxRows));

            var tenantId = new TenantId(item.Job.TenantId);
            var result = await sqlExec.ExecuteQueryBufferedAsync(
                queryRequest,
                item.User,
                tenantId,
                linkedCts.Token).ConfigureAwait(false);

            // Persist output to scratch directory (Pre-Storage DDM & RLS applied by GovernedSqlExecutionService)
            var tenantDir = Path.Combine(manager.ScratchDirectory, item.Job.TenantId);
            Directory.CreateDirectory(tenantDir);

            var ext = item.Job.Format.Equals("csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "json";
            var filePath = Path.Combine(tenantDir, $"{item.Job.JobId}.{ext}");

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
            manager.UpdateJob(completed);

            await RecordAuditAsync(completed, item.User, AuditEventTypes.AsyncJobCompleted, "ALLOW", "Job completed successfully.", stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var cancelled = runningJob with
            {
                State = AsyncJobState.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Job execution cancelled or timed out."
            };
            manager.UpdateJob(cancelled);
        }
        catch (Exception ex)
        {
            var sanitized = SecretScrubber.Scrub(ex.Message);
            var failed = runningJob with
            {
                State = AsyncJobState.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = sanitized
            };
            manager.UpdateJob(failed);
            _logger.LogError(ex, "Failed to execute async query job {JobId}", item.Job.JobId);
        }
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

    private static string EscapeCsv(string value)
    {
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
            var sidStr = user.FindFirst("sub")?.Value
                         ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                         ?? job.SubmittedByUserId;

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
