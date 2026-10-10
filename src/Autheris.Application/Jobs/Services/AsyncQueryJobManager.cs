namespace Autheris.Application.Jobs.Services;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Jobs.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class AsyncQueryJobManager : IAsyncQueryJobManager
{
    private readonly Channel<AsyncJobExecutionItem> _channel;
    private readonly ConcurrentDictionary<string, AsyncJobDescriptor> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCts = new();
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ILogger<AsyncQueryJobManager> _logger;
    private readonly string _scratchDirectory;

    public AsyncQueryJobManager(
        IAuditLogRepository auditLogRepository,
        ILogger<AsyncQueryJobManager>? logger = null,
        IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(auditLogRepository);
        _auditLogRepository = auditLogRepository;
        _logger = logger ?? NullLogger<AsyncQueryJobManager>.Instance;

        var boundedOptions = new BoundedChannelOptions(1_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<AsyncJobExecutionItem>(boundedOptions);

        var basePath = environment?.ContentRootPath ?? AppContext.BaseDirectory;
        _scratchDirectory = Path.GetFullPath(Path.Combine(basePath, "scratch", "jobs"));
        Directory.CreateDirectory(_scratchDirectory);
    }

    public ChannelReader<AsyncJobExecutionItem> ChannelReader => _channel.Reader;
    public string ScratchDirectory => _scratchDirectory;

    public void UpdateJob(AsyncJobDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _jobs[descriptor.JobId] = descriptor;
    }

    public async Task<AsyncJobDescriptor> SubmitJobAsync(
        AsyncQueryJobRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("Query cannot be empty.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(tenantId.Value))
        {
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        }

        var userSid = user.FindFirst("sub")?.Value
                      ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? "anonymous";

        var jobId = Guid.NewGuid().ToString("D");
        var format = string.IsNullOrWhiteSpace(request.Format) ? "json" : request.Format.Trim().ToLowerInvariant();

        var descriptor = new AsyncJobDescriptor(
            JobId: jobId,
            TenantId: tenantId.Value,
            SubmittedByUserId: userSid,
            Query: request.Query,
            Format: format,
            State: AsyncJobState.Queued,
            CreatedAt: DateTimeOffset.UtcNow);

        _jobs[jobId] = descriptor;

        var cts = new CancellationTokenSource();
        if (request.Timeout.HasValue && request.Timeout.Value > TimeSpan.Zero)
        {
            cts.CancelAfter(request.Timeout.Value);
        }
        _activeCts[jobId] = cts;

        await RecordAuditAsync(
            descriptor,
            user,
            AuditEventTypes.AsyncJobSubmitted,
            "ALLOW",
            "Job enqueued for background execution.",
            ct).ConfigureAwait(false);

        await _channel.Writer.WriteAsync(new AsyncJobExecutionItem(descriptor, request, user, cts), ct).ConfigureAwait(false);

        return descriptor;
    }

    public Task<AsyncJobDescriptor?> GetJobAsync(
        string jobId,
        TenantId tenantId,
        string userSid,
        CancellationToken ct = default)
    {
        ValidateJobId(jobId);

        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return Task.FromResult<AsyncJobDescriptor?>(null);
        }

        // Zero-IDOR isolation: return 404 (null) if tenant or user do not match
        if (!string.Equals(job.TenantId, tenantId.Value, StringComparison.Ordinal) ||
            !string.Equals(job.SubmittedByUserId, userSid, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Zero-IDOR access violation blocked for job {JobId}. Tenant mismatch or user mismatch.", jobId);
            return Task.FromResult<AsyncJobDescriptor?>(null);
        }

        return Task.FromResult<AsyncJobDescriptor?>(job);
    }

    public async Task<Stream?> GetJobResultStreamAsync(
        string jobId,
        TenantId tenantId,
        string userSid,
        CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
        if (job == null || job.State != AsyncJobState.Completed || string.IsNullOrWhiteSpace(job.ResultFilePath))
        {
            return null;
        }

        var fullResultPath = Path.GetFullPath(job.ResultFilePath);
        var fullScratchPath = Path.GetFullPath(_scratchDirectory);
        if (!fullResultPath.StartsWith(fullScratchPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("Unauthorized path traversal detected in job result retrieval.");
        }

        if (!File.Exists(fullResultPath))
        {
            return null;
        }

        await RecordAuditAsync(
            job,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userSid), new Claim("tenant_id", tenantId.Value)], "JobAuth")),
            AuditEventTypes.AsyncJobDownloaded,
            "ALLOW",
            "Job result stream downloaded.",
            ct).ConfigureAwait(false);

        return new FileStream(fullResultPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public async Task<bool> CancelJobAsync(
        string jobId,
        TenantId tenantId,
        string userSid,
        CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
        if (job == null)
        {
            return false;
        }

        if (job.State is AsyncJobState.Completed or AsyncJobState.Failed or AsyncJobState.Cancelled)
        {
            return false;
        }

        if (_activeCts.TryRemove(jobId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
            cts.Dispose();
        }

        var updated = job with
        {
            State = AsyncJobState.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = "Job cancelled by user request."
        };
        _jobs[jobId] = updated;

        if (!string.IsNullOrWhiteSpace(job.ResultFilePath) && File.Exists(job.ResultFilePath))
        {
            try { File.Delete(job.ResultFilePath); } catch { }
        }

        await RecordAuditAsync(
            updated,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userSid), new Claim("tenant_id", tenantId.Value)], "JobAuth")),
            AuditEventTypes.AsyncJobCancelled,
            "ALLOW",
            "Job cancelled.",
            ct).ConfigureAwait(false);

        return true;
    }

    public Task PurgeExpiredJobsAsync(TimeSpan retentionPeriod, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - retentionPeriod;
        foreach (var kvp in _jobs)
        {
            var job = kvp.Value;
            var isTerminal = job.State is AsyncJobState.Completed or AsyncJobState.Failed or AsyncJobState.Cancelled;
            if (isTerminal && (job.CompletedAt ?? job.CreatedAt) < cutoff)
            {
                if (_jobs.TryRemove(kvp.Key, out var removed))
                {
                    if (!string.IsNullOrWhiteSpace(removed.ResultFilePath) && File.Exists(removed.ResultFilePath))
                    {
                        try { File.Delete(removed.ResultFilePath); } catch { }
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private static void ValidateJobId(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) ||
            jobId.Contains("..", StringComparison.Ordinal) ||
            jobId.Contains('/', StringComparison.Ordinal) ||
            jobId.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid jobId. Path traversal sequences and separators are forbidden.", nameof(jobId));
        }
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
