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
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class AsyncQueryJobManager : IAsyncQueryJobManager, IAsyncJobQueue
{
    private readonly Channel<AsyncJobExecutionItem> _channel;
    private readonly ConcurrentDictionary<string, AsyncJobDescriptor> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCts = new();
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ILogger<AsyncQueryJobManager> _logger;
    private readonly string _scratchDirectory;
    private readonly IOptions<GatewayOptions>? _options;
    private readonly IOptions<AsyncJobOptions>? _jobOptions;
    private const int QueueCapacity = 1_000;
    private readonly object _submitGate = new();
    private readonly IDistributedClusterStateProvider? _cluster;
    private readonly AsyncJobNodeIdentity _node;
    private readonly TimeProvider _time;

    private static readonly System.Diagnostics.Metrics.Meter Meter = new("Autheris.AsyncJobs");
    private static readonly System.Diagnostics.Metrics.Counter<long> ClusterWriteFailures =
        Meter.CreateCounter<long>("autheris.asyncjobs.cluster_write_failures", description: "Async job record writes to the cluster state that failed.");

    /// <summary>Grace period after the job deadline before a job of an unreachable owner node is reported as failed.</summary>
    private static readonly TimeSpan OwnerLostGrace = TimeSpan.FromSeconds(60);

    public AsyncQueryJobManager(
        IAuditLogRepository auditLogRepository,
        ILogger<AsyncQueryJobManager>? logger = null,
        IHostEnvironment? environment = null,
        IOptions<GatewayOptions>? options = null,
        IOptions<AsyncJobOptions>? jobOptions = null,
        IDistributedClusterStateProvider? clusterState = null,
        IClusterStateTopology? topology = null,
        AsyncJobNodeIdentity? node = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(auditLogRepository);
        _options = options;
        _jobOptions = jobOptions;
        if (topology?.IsClusterWide == true && clusterState == null)
        {
            throw new InvalidOperationException("Async jobs in a cluster-wide topology require an IDistributedClusterStateProvider.");
        }

        _cluster = topology?.IsClusterWide == true ? clusterState : null;
        _node = node ?? new AsyncJobNodeIdentity();
        _time = timeProvider ?? TimeProvider.System;
        _auditLogRepository = auditLogRepository;
        _logger = logger ?? NullLogger<AsyncQueryJobManager>.Instance;

        var boundedOptions = new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<AsyncJobExecutionItem>(boundedOptions);

        var configured = (_jobOptions?.Value ?? _options?.Value?.AsyncJobs)?.ScratchDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _scratchDirectory = Path.GetFullPath(configured);
        }
        else
        {
            var basePath = environment?.ContentRootPath ?? AppContext.BaseDirectory;
            _scratchDirectory = Path.GetFullPath(Path.Combine(basePath, "scratch", "jobs"));
        }

        Directory.CreateDirectory(_scratchDirectory);
    }

    public ChannelReader<AsyncJobExecutionItem> Reader => _channel.Reader;
    public string ScratchDirectory => _scratchDirectory;

    /// <summary>Unconditionally stores a descriptor (seeding and tests); the worker uses <see cref="TryTransitionAsync"/>.</summary>
    public void UpdateJob(AsyncJobDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _jobs[descriptor.JobId] = descriptor;
    }

    public async ValueTask<bool> TryTransitionAsync(AsyncJobDescriptor next, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (_cluster is not null && next.State != AsyncJobState.Cancelled)
        {
            // A cancel issued on another node is recorded as a tombstone; it wins over every later owner write
            // (Running as well as Completed or Failed).
            AsyncJobCancelTombstone? tombstone = null;
            try
            {
                tombstone = await _cluster.GetAsync<AsyncJobCancelTombstone>(TombstoneKey(next.TenantId, next.JobId), ct).ConfigureAwait(false);
            }
            catch (ClusterStateUnavailableException ex)
            {
                _logger.LogWarning(ex, "Cluster state unavailable while checking job {JobId} for a remote cancel.", next.JobId);
            }

            if (tombstone is not null)
            {
                var cancelled = next with
                {
                    State = AsyncJobState.Cancelled,
                    CompletedAt = tombstone.CancelledAt,
                    ErrorMessage = "Job cancelled by user request.",
                    ResultFilePath = null
                };
                DeleteQuietly(next.ResultFilePath);
                if (TryTransitionLocal(cancelled))
                {
                    await WriteRecordAsync(cancelled, throwOnFailure: false, ct).ConfigureAwait(false);
                }

                return false;
            }
        }

        if (!TryTransitionLocal(next))
        {
            return false;
        }

        await WriteRecordAsync(next, throwOnFailure: false, ct).ConfigureAwait(false);
        return true;
    }

    private static string TombstoneKey(string tenantId, string jobId) => $"asyncjob:{tenantId}:{jobId}:cancel";

    private static void DeleteQuietly(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static string RecordKey(string tenantId, string jobId) => $"asyncjob:{tenantId}:{jobId}";

    private TimeSpan RecordTtl
    {
        get
        {
            var jobs = _jobOptions?.Value ?? _options?.Value?.AsyncJobs ?? new AsyncJobOptions();
            return TimeSpan.FromSeconds(Math.Max(1, jobs.MaxTimeoutSeconds)) + TimeSpan.FromHours(Math.Max(1, jobs.RetentionHours));
        }
    }

    private AsyncJobClusterRecord ToRecord(AsyncJobDescriptor d) => new(
        d.JobId,
        d.TenantId,
        d.SubmittedByUserId,
        d.Format,
        d.State,
        d.CreatedAt,
        d.StartedAt,
        d.CompletedAt,
        d.RowsProduced,
        d.BytesProduced,
        d.ErrorMessage,
        string.IsNullOrWhiteSpace(d.ResultFilePath) ? null : Path.GetFileName(d.ResultFilePath),
        _node.NodeId);

    /// <summary>Writes the cluster record of a job (TTL is reset on every write). A failure is fatal only for the submit.</summary>
    private async ValueTask WriteRecordAsync(AsyncJobDescriptor descriptor, bool throwOnFailure, CancellationToken ct)
    {
        if (_cluster is null)
        {
            return;
        }

        try
        {
            await _cluster.SetAsync(RecordKey(descriptor.TenantId, descriptor.JobId), ToRecord(descriptor), RecordTtl, ct).ConfigureAwait(false);
        }
        catch (ClusterStateUnavailableException ex)
        {
            if (throwOnFailure)
            {
                throw;
            }

            ClusterWriteFailures.Add(1);
            _logger.LogError(ex, "Could not write the cluster record of job {JobId}; the local state is kept.", descriptor.JobId);
        }
    }

    private static bool IsTerminal(AsyncJobState state) =>
        state is AsyncJobState.Completed or AsyncJobState.Failed or AsyncJobState.Cancelled;

    /// <summary>Compare-and-swap that never leaves a terminal state (Completed, Failed, Cancelled stay terminal).</summary>
    private bool TryTransitionLocal(AsyncJobDescriptor next)
    {
        while (true)
        {
            if (!_jobs.TryGetValue(next.JobId, out var current))
            {
                if (_jobs.TryAdd(next.JobId, next))
                {
                    return true;
                }

                continue;
            }

            if (IsTerminal(current.State))
            {
                return false;
            }

            if (_jobs.TryUpdate(next.JobId, next, current))
            {
                if (IsTerminal(next.State) && _activeCts.TryRemove(next.JobId, out var cts))
                {
                    // A finished job needs no timeout source; a cancelled one must stop its running query.
                    try
                    {
                        if (next.State == AsyncJobState.Cancelled)
                        {
                            cts.Cancel();
                        }
                    }
                    catch (ObjectDisposedException) { }

                    cts.Dispose();
                }

                return true;
            }
        }
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

        // Canonical owner (same resolution as the status/result/cancel endpoints). Fail-closed: a principal without a
        // resolvable identity must not become a shared "anonymous" owner.
        var userSid = AsyncJobOwner.Resolve(user)
                      ?? throw new Autheris.Domain.Exceptions.GatewaySecurityException("Async jobs require an authenticated principal with a resolvable user identity.", "ASYNC_JOB_OWNER_UNRESOLVABLE");

        var jobId = Guid.NewGuid().ToString("D");
        var format = string.IsNullOrWhiteSpace(request.Format) ? "json" : request.Format.Trim().ToLowerInvariant();

        var descriptor = new AsyncJobDescriptor(
            JobId: jobId,
            TenantId: tenantId.Value,
            SubmittedByUserId: userSid,
            Query: request.Query,
            Format: format,
            State: AsyncJobState.Queued,
            CreatedAt: _time.GetUtcNow());

        // Per-tenant backlog cap: counted and reserved under one gate so concurrent submits cannot overshoot it.
        var maxQueued = (_jobOptions?.Value ?? _options?.Value?.AsyncJobs ?? new AsyncJobOptions()).MaxQueuedJobsPerTenant;
        lock (_submitGate)
        {
            var active = 0;
            foreach (var existing in _jobs.Values)
            {
                if (!IsTerminal(existing.State) && string.Equals(existing.TenantId, tenantId.Value, StringComparison.Ordinal))
                {
                    active++;
                }
            }

            if (active >= maxQueued)
            {
                throw new AsyncJobQueueLimitExceededException(maxQueued);
            }

            _jobs[jobId] = descriptor;
        }

        // SR-H-05: row count and timeout are server-bounded; the client values only narrow them.
        request = AsyncJobLimits.Normalize(request, _options?.Value);
        var cts = new CancellationTokenSource();
        cts.CancelAfter(request.Timeout!.Value);
        _activeCts[jobId] = cts;

        var recordWritten = false;
        try
        {
            // Fail closed: when the shared state cannot take the record, no other replica could ever see this job.
            await WriteRecordAsync(descriptor, throwOnFailure: true, ct).ConfigureAwait(false);
            recordWritten = _cluster is not null;
            await RecordAuditAsync(
                descriptor,
                user,
                AuditEventTypes.AsyncJobSubmitted,
                "ALLOW",
                "Job enqueued for background execution.",
                ct).ConfigureAwait(false);

            // Bounded queue: a full channel rejects instead of blocking the request (the cleanup below removes the job).
            if (!_channel.Writer.TryWrite(new AsyncJobExecutionItem(descriptor, request, user, cts)))
            {
                throw new AsyncJobQueueLimitExceededException("The async job queue of this node is full. Retry later.", QueueCapacity);
            }
        }
        catch
        {
            // Fail-closed: no audit record, no job.
            _jobs.TryRemove(jobId, out _);
            if (recordWritten)
            {
                try { await _cluster!.RemoveAsync(RecordKey(descriptor.TenantId, jobId), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Could not remove the cluster record of rejected job {JobId}.", jobId); }
            }

            if (_activeCts.TryRemove(jobId, out var failedCts))
            {
                failedCts.Dispose();
            }
            throw;
        }

        return descriptor;
    }

    public async Task<AsyncJobDescriptor?> GetJobAsync(
        string jobId,
        TenantId tenantId,
        string userSid,
        CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
        return resolved?.Descriptor;
    }

    private sealed record ResolvedJob(AsyncJobDescriptor Descriptor, AsyncJobClusterRecord? Record);

    /// <summary>
    /// Finds a job visible to the requester: the local entry wins, otherwise the cluster record under the requester's
    /// tenant key. Tenant and owner are verified in both cases (Zero-IDOR: a mismatch is indistinguishable from "absent").
    /// </summary>
    private async Task<ResolvedJob?> ResolveAsync(string jobId, TenantId tenantId, string userSid, CancellationToken ct)
    {
        ValidateJobId(jobId);

        if (_jobs.TryGetValue(jobId, out var job))
        {
            // Zero-IDOR isolation: return 404 (null) if tenant or user do not match
            if (!string.Equals(job.TenantId, tenantId.Value, StringComparison.Ordinal) ||
                !string.Equals(job.SubmittedByUserId, userSid, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Zero-IDOR access violation blocked for job {JobId}. Tenant mismatch or user mismatch.", jobId);
                await RecordAccessDeniedAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
                return null;
            }

            return new ResolvedJob(job, null);
        }

        if (_cluster is null || string.IsNullOrWhiteSpace(tenantId.Value) || !Guid.TryParseExact(jobId, "D", out _))
        {
            return null;
        }

        // A failure of the shared state propagates: "unknown" must never be reported as "not found".
        var record = await _cluster.GetAsync<AsyncJobClusterRecord>(RecordKey(tenantId.Value, jobId), ct).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        if (!string.Equals(record.TenantId, tenantId.Value, StringComparison.Ordinal) ||
            !string.Equals(record.OwnerSid, userSid, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Zero-IDOR access violation blocked for job {JobId}. Tenant mismatch or user mismatch.", jobId);
            await RecordAccessDeniedAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
            return null;
        }

        // The cancel tombstone is authoritative while the job has not reached a terminal state: owner writes cannot mask it.
        var tombstone = await _cluster.GetAsync<AsyncJobCancelTombstone>(TombstoneKey(tenantId.Value, jobId), ct).ConfigureAwait(false);
        if (tombstone is not null && record.State is AsyncJobState.Queued or AsyncJobState.Running)
        {
            record = record with
            {
                State = AsyncJobState.Cancelled,
                CompletedAt = tombstone.CancelledAt,
                ErrorMessage = "Job cancelled by user request.",
                ResultFileName = null
            };
        }

        return new ResolvedJob(ToDescriptor(record), record);
    }

    private AsyncJobDescriptor ToDescriptor(AsyncJobClusterRecord record)
    {
        var descriptor = new AsyncJobDescriptor(
            record.JobId,
            record.TenantId,
            record.OwnerSid,
            Query: string.Empty,
            record.Format,
            record.State,
            record.CreatedAt,
            record.StartedAt,
            record.CompletedAt,
            ResultFilePath: null,
            record.RowsProduced,
            record.BytesProduced,
            record.ErrorMessage);

        // The owner node vanished without a terminal write: computed, never persisted.
        var jobs = _jobOptions?.Value ?? _options?.Value?.AsyncJobs ?? new AsyncJobOptions();
        var deadline = record.CreatedAt + TimeSpan.FromSeconds(Math.Max(1, jobs.MaxTimeoutSeconds)) + OwnerLostGrace;
        if (record.State is AsyncJobState.Queued or AsyncJobState.Running && _time.GetUtcNow() > deadline)
        {
            return descriptor with { State = AsyncJobState.Failed, CompletedAt = deadline, ErrorMessage = "job owner node lost" };
        }

        return descriptor;
    }

    public async Task<Stream?> GetJobResultStreamAsync(
        string jobId,
        TenantId tenantId,
        string userSid,
        CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
        var job = resolved?.Descriptor;
        if (job == null || job.State != AsyncJobState.Completed)
        {
            return null;
        }

        string fullResultPath;
        if (resolved!.Record is { } record)
        {
            // The job lives on another replica: only a shared scratch directory can serve its result.
            var jobOptions = _jobOptions?.Value ?? _options?.Value?.AsyncJobs ?? new AsyncJobOptions();
            if (string.IsNullOrWhiteSpace(record.ResultFileName))
            {
                return null;
            }

            if (!jobOptions.SharedScratchDirectory)
            {
                throw new AsyncJobResultUnavailableException();
            }

            // The file name comes from shared state: it must be exactly "{jobId}.csv|json", never a path.
            var expectedJson = jobId + ".json";
            var expectedCsv = jobId + ".csv";
            if (!string.Equals(record.ResultFileName, expectedJson, StringComparison.Ordinal) &&
                !string.Equals(record.ResultFileName, expectedCsv, StringComparison.Ordinal))
            {
                await DenyResultAsync(job, tenantId, userSid, "Job result file name in the cluster record is invalid.", ct).ConfigureAwait(false);
                throw new Autheris.Domain.Exceptions.GatewaySecurityException("Unauthorized path traversal detected in job result retrieval.", "JOB_RESULT_PATH_FORBIDDEN");
            }

            fullResultPath = Path.GetFullPath(Path.Combine(_scratchDirectory, record.TenantId, record.ResultFileName));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(job.ResultFilePath))
            {
                return null;
            }

            fullResultPath = Path.GetFullPath(job.ResultFilePath);
        }

        var fullScratchPath = Path.GetFullPath(_scratchDirectory);
        // Containment needs a trailing separator: ".../jobs-evil/x" must not match the ".../jobs" prefix.
        var scratchPrefix = Path.TrimEndingDirectorySeparator(fullScratchPath) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullResultPath.StartsWith(scratchPrefix, comparison))
        {
            await DenyResultAsync(job, tenantId, userSid, "Job result path is outside the scratch directory.", ct).ConfigureAwait(false);
            throw new Autheris.Domain.Exceptions.GatewaySecurityException("Unauthorized path traversal detected in job result retrieval.", "JOB_RESULT_PATH_FORBIDDEN");
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

    private Task DenyResultAsync(AsyncJobDescriptor job, TenantId tenantId, string userSid, string message, CancellationToken ct) =>
        RecordAuditAsync(
            job,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userSid), new Claim("tenant_id", tenantId.Value)], "JobAuth")),
            AuditEventTypes.AsyncJobDownloaded,
            "DENY",
            message,
            ct);

    public async Task<bool> CancelJobAsync(
        string jobId,
        TenantId tenantId,
        string userSid,
        CancellationToken ct = default)
    {
        // Owner and tenant are verified first (local entry or cluster record), exactly like the status route.
        var resolved = await ResolveAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
        if (resolved == null)
        {
            return false;
        }

        var job = resolved.Descriptor;
        if (job.State is AsyncJobState.Completed or AsyncJobState.Failed or AsyncJobState.Cancelled)
        {
            return false;
        }

        if (resolved.Record is { } remote)
        {
            return await CancelRemoteAsync(remote, tenantId, userSid, ct).ConfigureAwait(false);
        }

        var updated = job with
        {
            State = AsyncJobState.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = "Job cancelled by user request."
        };

        // Terminal states stay terminal: when the worker finished first, the cancel is a no-op.
        if (!TryTransitionLocal(updated))
        {
            return false;
        }

        await WriteRecordAsync(updated, throwOnFailure: false, ct).ConfigureAwait(false);

        if (_activeCts.TryRemove(jobId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
            cts.Dispose();
        }

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

    public async ValueTask SweepOrphanedScratchFilesAsync(CancellationToken ct)
    {
        var jobs = _jobOptions?.Value ?? _options?.Value?.AsyncJobs ?? new AsyncJobOptions();
        var now = DateTime.UtcNow;
        var orphanAge = TimeSpan.FromSeconds(Math.Max(1, jobs.MaxTimeoutSeconds));
        var maxAge = TimeSpan.FromHours(Math.Max(0, jobs.RetentionHours));

        if (!Directory.Exists(_scratchDirectory))
        {
            return;
        }

        foreach (var tenantDir in Directory.EnumerateDirectories(_scratchDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (IsReparsePoint(tenantDir))
            {
                continue;
            }

            var tenantId = Path.GetFileName(tenantDir);
            foreach (var file in Directory.EnumerateFiles(tenantDir))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                var ext = Path.GetExtension(name);
                var stem = Path.GetFileNameWithoutExtension(name);
                if (!(string.Equals(ext, ".csv", StringComparison.Ordinal) || string.Equals(ext, ".json", StringComparison.Ordinal)) ||
                    !Guid.TryParseExact(stem, "D", out _) ||
                    IsReparsePoint(file))
                {
                    continue;
                }

                try
                {
                    var age = now - File.GetLastWriteTimeUtc(file);
                    var delete = age > maxAge;
                    if (!delete && age > orphanAge)
                    {
                        delete = !await IsJobKnownAsync(tenantId, stem, ct).ConfigureAwait(false);
                    }

                    if (delete)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not sweep scratch file {FileName}.", name);
                }
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private async ValueTask<bool> IsJobKnownAsync(string tenantId, string jobId, CancellationToken ct)
    {
        if (_jobs.TryGetValue(jobId, out var job) && string.Equals(job.TenantId, tenantId, StringComparison.Ordinal))
        {
            return true;
        }

        if (_cluster is null)
        {
            return false;
        }

        try
        {
            return await _cluster.GetAsync<AsyncJobClusterRecord>(RecordKey(tenantId, jobId), ct).ConfigureAwait(false) is not null;
        }
        catch (ClusterStateUnavailableException)
        {
            // Unknown, not absent: keep the file; only the retention age may delete it.
            return true;
        }
    }

    /// <summary>Cancels a job owned by another node: a tombstone is written and the owner is signalled.</summary>
    private async Task<bool> CancelRemoteAsync(AsyncJobClusterRecord record, TenantId tenantId, string userSid, CancellationToken ct)
    {
        // The record handed in may be stale: re-read right before deciding, and never cancel a finished job.
        var current = await _cluster!.GetAsync<AsyncJobClusterRecord>(RecordKey(record.TenantId, record.JobId), ct).ConfigureAwait(false);
        if (current is null || current.State is AsyncJobState.Completed or AsyncJobState.Failed or AsyncJobState.Cancelled)
        {
            return false;
        }

        // Authoritative state first (fail closed: an unreachable state surfaces as 503), then the best-effort signal.
        var tombstone = new AsyncJobCancelTombstone(record.TenantId, record.JobId, userSid, _time.GetUtcNow());
        await _cluster.SetAsync(TombstoneKey(record.TenantId, record.JobId), tombstone, RecordTtl, ct).ConfigureAwait(false);
        try
        {
            await _cluster.PublishEventAsync(AsyncJobCancelSignal.Channel, new AsyncJobCancelSignal(record.TenantId, record.JobId, userSid), ct).ConfigureAwait(false);
        }
        catch (ClusterStateUnavailableException ex)
        {
            _logger.LogWarning(ex, "Could not signal the owner node of job {JobId}; the tombstone stops the job at its next state write.", record.JobId);
        }

        var cancelled = current with
        {
            State = AsyncJobState.Cancelled,
            CompletedAt = tombstone.CancelledAt,
            ErrorMessage = "Job cancelled by user request.",
            ResultFileName = null
        };
        await RecordAuditAsync(
            ToDescriptor(cancelled),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userSid), new Claim("tenant_id", tenantId.Value)], "JobAuth")),
            AuditEventTypes.AsyncJobCancelled,
            "ALLOW",
            "Job cancelled.",
            ct).ConfigureAwait(false);

        return true;
    }

    public async ValueTask ApplyCancelSignalAsync(AsyncJobCancelSignal signal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (_cluster is null ||
            !_jobs.TryGetValue(signal.JobId, out var job) ||
            !string.Equals(job.TenantId, signal.TenantId, StringComparison.Ordinal) ||
            !string.Equals(job.SubmittedByUserId, signal.RequesterSid, StringComparison.OrdinalIgnoreCase))
        {
            // Not our job, or not requested by its owner: a forged or misrouted signal changes nothing.
            _logger.LogWarning("Ignored async job cancel signal for job {JobId}: unknown job or requester is not the owner.", signal.JobId);
            return;
        }

        // The signal is only a hint: act solely when the shared state confirms the cancel (unavailable state = ignore).
        try
        {
            var tombstone = await _cluster.GetAsync<AsyncJobCancelTombstone>(TombstoneKey(signal.TenantId, signal.JobId), ct).ConfigureAwait(false);
            if (tombstone is null || !string.Equals(tombstone.RequesterSid, signal.RequesterSid, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Ignored async job cancel signal for job {JobId}: no matching cancel tombstone.", signal.JobId);
                return;
            }
        }
        catch (ClusterStateUnavailableException ex)
        {
            _logger.LogWarning(ex, "Ignored async job cancel signal for job {JobId}: cluster state unavailable.", signal.JobId);
            return;
        }

        var cancelled = job with
        {
            State = AsyncJobState.Cancelled,
            CompletedAt = _time.GetUtcNow(),
            ErrorMessage = "Job cancelled by user request.",
            ResultFilePath = null
        };
        if (!TryTransitionLocal(cancelled))
        {
            return;
        }

        DeleteQuietly(job.ResultFilePath);
        await WriteRecordAsync(cancelled, throwOnFailure: false, ct).ConfigureAwait(false);
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
                    Message = message
                })
            };

            await _auditLogRepository.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record audit event for job {JobId}", job.JobId);
            if (string.Equals(decision, "ALLOW", StringComparison.Ordinal))
            {
                // Fail-closed: an ALLOW outcome (e.g. a result download) must never happen without its audit record.
                throw;
            }
        }
    }

    /// <summary>
    /// Records a DENY event for a Zero-IDOR mismatch. Attributed to the requesting tenant/actor only;
    /// the foreign owner and tenant of the job are never written. Best effort: the denial itself is already enforced.
    /// </summary>
    private async Task RecordAccessDeniedAsync(string jobId, TenantId requesterTenant, string requesterSid, CancellationToken ct)
    {
        try
        {
            var entry = new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                TenantId = requesterTenant,
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = AuditEventTypes.AsyncJobAccessDenied,
                ActorSid = new Sid(string.IsNullOrWhiteSpace(requesterSid) ? "anonymous" : requesterSid),
                TargetTable = $"job:{jobId}",
                Decision = "DENY",
                DetailsJson = JsonSerializer.Serialize(new { JobId = jobId, Message = "Job not accessible for the requesting principal." })
            };

            await _auditLogRepository.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to record access-denied audit event for job {JobId}", jobId);
        }
    }
}
