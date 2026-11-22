namespace Autheris.Application.Runtime.Jobs.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Runtime.Jobs.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.State;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Usage;
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
    private readonly IOptions<AsyncJobOptions>? _jobOptions;
    private readonly IDistributedClusterStateProvider? _clusterState;
    private readonly IOptionsMonitor<AsyncJobOptions>? _jobOptionsMonitor;
    private readonly Autheris.Application.Mcp.Interfaces.IReadPathSwitches? _switches;

    public AsyncQueryJobBackgroundWorker(
        IAsyncQueryJobManager jobManager,
        IAsyncJobQueue queue,
        IServiceScopeFactory scopeFactory,
        IAuditLogRepository auditLogRepository,
        ILogger<AsyncQueryJobBackgroundWorker>? logger = null,
        IOptions<GatewayOptions>? options = null,
        IOptions<AsyncJobOptions>? jobOptions = null,
        IDistributedClusterStateProvider? clusterState = null,
        IClusterStateTopology? topology = null,
        IOptionsMonitor<AsyncJobOptions>? jobOptionsMonitor = null,
        Autheris.Application.Mcp.Interfaces.IReadPathSwitches? switches = null)
    {
        _options = options;
        _jobOptions = jobOptions;
        _clusterState = topology?.IsClusterWide == true ? clusterState : null;
        _jobOptionsMonitor = jobOptionsMonitor;
        _switches = switches;
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // No new jobs are accepted once the worker stops (they would never run).
        _queue.Complete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsAsyncJobsEnabled())
        {
            _logger.LogInformation("AsyncQueryJobBackgroundWorker is disabled by configuration; no jobs will be dequeued.");
            return;
        }

        _logger.LogInformation("AsyncQueryJobBackgroundWorker started.");

        // Cancel requests for jobs running here that were issued on another node.
        IAsyncDisposable? cancelSubscription = null;
        try
        {
            cancelSubscription = _clusterState?.SubscribeAsync<AsyncJobCancelSignal>(
                AsyncJobCancelSignal.Channel,
                signal => _queue.ApplyCancelSignalAsync(signal, stoppingToken),
                stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without the subscription the owner still honours the cancel tombstone at its next state write.
            SubscribeFailures.Add(1);
            _logger.LogError(ex, "Could not subscribe to async job cancel signals; remote cancels apply at the next state write.");
        }

        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _queue.Complete();
            if (cancelSubscription is not null)
            {
                await cancelSubscription.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static readonly System.Diagnostics.Metrics.Meter Meter = new("Autheris.AsyncJobs");
    private static readonly System.Diagnostics.Metrics.Counter<long> SubscribeFailures =
        Meter.CreateCounter<long>("autheris.asyncjobs.subscribe_failures", description: "Failed subscriptions to the async job cancel channel.");

    private async Task RunAsync(CancellationToken stoppingToken)
    {

        try
        {
            await _queue.SweepOrphanedScratchFilesAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Sweeping orphaned async job scratch files failed.");
        }

        var purgeTask = RunPurgeLoopAsync(stoppingToken);

        try
        {
            var jobs = JobOptions;
            using var scheduler = new AsyncJobScheduler(
                jobs.MaxConcurrentJobs,
                jobs.MaxConcurrentJobsPerTenant,
                ProcessJobItemAsync,
                _logger,
                DiscardQueuedAsync,
                IsAsyncJobsEnabled);
            await scheduler.RunAsync(_queue.Reader, stoppingToken).ConfigureAwait(false);
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

    private AsyncJobOptions JobOptions => _jobOptionsMonitor?.CurrentValue ?? _jobOptions?.Value ?? _options?.Value?.AsyncJobs ?? new AsyncJobOptions();

    private bool IsAsyncJobsEnabled() => _switches?.AsyncJobsEnabled ?? true;

    /// <summary>A job still queued at shutdown is cancelled: the in-memory queue does not survive the process.</summary>
    private async Task DiscardQueuedAsync(AsyncJobExecutionItem item)
    {
        var cancelled = item.Job with
        {
            State = AsyncJobState.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = "Job cancelled because the gateway is shutting down."
        };
        await _queue.TryTransitionAsync(cancelled, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>SR-M-21: periodically removes finished jobs and their result files after the retention period.</summary>
    private async Task RunPurgeLoopAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, JobOptions.PurgeIntervalMinutes));
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
        var retention = TimeSpan.FromHours(Math.Max(0, JobOptions.RetentionHours));
        try
        {
            await _jobManager.PurgeExpiredJobsAsync(retention, ct).ConfigureAwait(false);
            await _queue.SweepOrphanedScratchFilesAsync(ct).ConfigureAwait(false);
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
        if (!IsAsyncJobsEnabled())
        {
            return;
        }

        // State writes must still work while the host is stopping: they get their own short deadline.
        using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var writeCt = writeCts.Token;
        // Captured first: a concurrent cancel disposes the source, and reading its token afterwards would throw.
        CancellationToken itemToken;
        try
        {
            itemToken = item.Cts.Token;
        }
        catch (ObjectDisposedException)
        {
            itemToken = new CancellationToken(true);
        }

        if (itemToken.IsCancellationRequested)
        {
            // Timed out or cancelled before it could start: nothing ran, the job ends Cancelled without a transient Running state.
            await _queue.TryTransitionAsync(item.Job with { State = AsyncJobState.Cancelled, CompletedAt = DateTimeOffset.UtcNow, ErrorMessage = "Job execution cancelled or timed out." }, writeCt).ConfigureAwait(false);
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var runningJob = item.Job with
        {
            State = AsyncJobState.Running,
            StartedAt = startedAt
        };
        if (!await _queue.TryTransitionAsync(runningJob, writeCt).ConfigureAwait(false))
        {
            // Cancelled (or otherwise finished) while still queued: nothing to execute.
            return;
        }

        if (itemToken.IsCancellationRequested)
        {
            // Cancelled concurrently while transitioning to Running: nothing ran, the job ends Cancelled.
            await _queue.TryTransitionAsync(runningJob with { State = AsyncJobState.Cancelled, CompletedAt = DateTimeOffset.UtcNow, ErrorMessage = "Job execution cancelled or timed out." }, writeCt).ConfigureAwait(false);
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, itemToken);
        string? writtenPath = null;
        long usageBytes = 0;
        int usageStatusCode = 200;
        long startTimestamp = Stopwatch.GetTimestamp();
        UsageRequestScope? usageScope = null;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tenantId = new TenantId(item.Job.TenantId);
            var userSid = new Sid(item.Job.SubmittedByUserId);
            IReadOnlySet<Sid> groupSids = item.Job.SubmittedByGroupSids is { Count: > 0 } rawGroups
                ? new HashSet<Sid>(rawGroups.Select(g => new Sid(g)))
                : new HashSet<Sid>();
            var usageAggregator = scope.ServiceProvider.GetService<IUsageAggregator>();
            if (usageAggregator != null || UsageRequestScope.DefaultAggregator != null)
            {
                var scopeTransport = item.Ingress == Autheris.Domain.Options.DataSources.DataSourceTransportFlags.None
                    ? Autheris.Domain.Options.DataSources.DataSourceTransportFlags.Rest
                    : item.Ingress;
                usageScope = UsageRequestScope.Begin(
                    new UsageIdentity(tenantId, userSid, groupSids),
                    scopeTransport,
                    caller: null,
                    aggregator: usageAggregator);
            }

            // Issue #447: the worker scope carries the ingress persisted with the job at submit. An item without one
            // (None), or a host without the context registration, leaves the ingress unset, which the transport gate denies (fail-closed).
            if (scope.ServiceProvider.GetService<Autheris.Domain.Audit.AuditContext>() is { } auditContext)
            {
                // a transport denial of this job is audited for the submitting caller and tenant
                auditContext.TenantId = tenantId;
                auditContext.ActorSid = userSid;
            }

            if (item.Ingress != Autheris.Domain.Options.DataSources.DataSourceTransportFlags.None)
            {
                scope.ServiceProvider.GetService<Autheris.Application.DataSources.IIngressTransportContext>()?.Set(item.Ingress);
            }
            var sqlExec = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

            // SR-H-05 defence in depth: re-derive the bounds server-side; the queued request is never trusted.
            var options = _options?.Value;
            linkedCts.CancelAfter(AsyncJobLimits.ResolveTimeout(item.Request, options, JobOptions));
            var queryRequest = new GovernedSqlQueryRequest(
                Sql: item.Request.Query,
                RowLimit: AsyncJobLimits.ResolveRowLimit(item.Request, options),
                Transport: SqlTransports.AsyncJob);

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
            usageBytes = fileInfo.Length;
            var completed = runningJob with
            {
                State = AsyncJobState.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                ResultFilePath = filePath,
                RowsProduced = result.RowCount,
                BytesProduced = fileInfo.Length
            };
            if (!await _queue.TryTransitionAsync(completed, writeCt).ConfigureAwait(false))
            {
                usageStatusCode = 499;
                // Lost the race against a cancel: the job stays terminal and its result must not outlive it.
                try { File.Delete(filePath); } catch (Exception ex) { _logger.LogWarning(ex, "Could not delete result of cancelled job {JobId}", item.Job.JobId); }
                return;
            }

            await RecordAuditAsync(completed, item.User, AuditEventTypes.AsyncJobCompleted, "ALLOW", "Job completed successfully.", writeCt).ConfigureAwait(false);
        }
        // ParseCanceledException derives from OperationCanceledException: a governed SQL error (syntax, parser timeout) is a failed job.
        catch (OperationCanceledException ex) when (!Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.TryMap(ex, out _))
        {
            usageStatusCode = 499;
            _logger.LogDebug(ex, "Async job {JobId} cancelled or timed out.", item.Job.JobId);
            DeletePartialResult(writtenPath);
            var cancelled = runningJob with
            {
                State = AsyncJobState.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Job execution cancelled or timed out."
            };
            await _queue.TryTransitionAsync(cancelled, writeCt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            usageStatusCode = 500;
            DeletePartialResult(writtenPath);
            // WP-X0a: the persisted message is the fixed message of the stable code, never Exception.Message (driver text, values).
            var sanitized = Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.FixedMessageOrDefault(ex, "The job could not be executed. Contact support with the trace id.");
            var failed = runningJob with
            {
                State = AsyncJobState.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = sanitized
            };
            await _queue.TryTransitionAsync(failed, writeCt).ConfigureAwait(false);
            _logger.LogError(ex, "Failed to execute async query job {JobId}", item.Job.JobId);
        }
        finally
        {
            if (usageScope != null)
            {
                long elapsedMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                usageScope.Complete(elapsedMs, usageBytes, usageStatusCode);
                usageScope.Dispose();
            }
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

    internal static async Task WriteCsvResultAsync(
        string filePath,
        GovernedSqlResult result,
        CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream);

        // Header (escaped for formula injection & delimiter/quote safety)
        var header = new string[result.Columns.Count];
        for (int i = 0; i < result.Columns.Count; i++)
        {
            header[i] = EscapeCsv(result.Columns[i]);
        }
        await writer.WriteLineAsync(string.Join(",", header)).ConfigureAwait(false);

        // Rows
        foreach (var row in result.Rows)
        {
            ct.ThrowIfCancellationRequested();
            var values = new string[result.Columns.Count];
            for (int i = 0; i < result.Columns.Count; i++)
            {
                var col = result.Columns[i];
                var val = row.TryGetValue(col, out var obj) ? FormatCsvValue(obj) : string.Empty;
                values[i] = EscapeCsv(val);
            }
            await writer.WriteLineAsync(string.Join(",", values)).ConfigureAwait(false);
        }
    }

    internal static string FormatCsvValue(object? value) =>
        value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

    internal static string EscapeCsv(string value) =>
        UsageCsvWriter.FormatCell(value, preserveSignedNumbers: true);

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
